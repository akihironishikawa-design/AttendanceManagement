using System.Text.RegularExpressions;
using NPOI.SS.UserModel;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Naming;

namespace TakaneAttendance.Core.Parsing;

/// <summary>
/// タイムレコーダー(MB20)出力の「出席記録」シートの解析。
///
/// 実サンプル「[インポート]タイムレコーダーデータ.xls」で確認した構造:
///   A3      : "勤怠時間" / C3 : 期間 "2026-07-01 ~ 2026-07-07"
///   A4:G4   : 日番号 1..7 (月次出力では 1..31)
///   社員ブロック(2行構成):
///     1行目(メタ行) : A="作業番号:" C=番号 / I="名前:" K=氏名 / S="部門:" U=部門
///     2行目(打刻行) : 日別セルに "06:4917:07" のように出退勤が連結された文字列
///
///   提出用の出席記録レポートは3行構成(メタ行・シフト行・打刻行)になるため、
///   メタ行間の行数から打刻行を判定する。
///
/// 打刻セルは丸めずに原文を保持する(出席記録レポートへそのまま出力するため)。
/// </summary>
public sealed class PunchParser
{
    /// <summary>"06:49" のような時刻を全て拾う。連結・改行のどちらにも対応する。</summary>
    private static readonly Regex TimePattern = new(@"(\d{1,2}):(\d{2})", RegexOptions.Compiled);

    private static readonly Regex IsoDatePattern =
        new(@"(\d{4})[-/](\d{1,2})[-/](\d{1,2})", RegexOptions.Compiled);

    private readonly NameNormalizer _normalizer;
    private readonly AliasMaster _alias;

    public PunchParser(NameNormalizer normalizer, AliasMaster alias)
    {
        _normalizer = normalizer;
        _alias = alias;
    }

    /// <summary>既定で解析するシート名(部分一致)。</summary>
    public const string DefaultSheetKeyword = "出席記録";

    /// <summary>ブック内から「出席記録」シートを推定する。</summary>
    public static string? GuessSheetName(string path)
    {
        var names = ExcelHelper.SheetNames(path);
        return names.FirstOrDefault(n => n.Contains(DefaultSheetKeyword)) ?? names.FirstOrDefault();
    }

    public PunchParseResult Parse(string path, string? sheetName, (int Year, int Month)? overrideYearMonth = null)
    {
        var result = new PunchParseResult();
        using var wb = ExcelHelper.OpenWorkbook(path);

        var sheet = sheetName != null ? wb.GetSheet(sheetName) : wb.GetSheetAt(0);
        if (sheet == null)
        {
            result.Messages.Add($"[E-PU-001] シート '{sheetName}' が見つかりません。");
            return result;
        }
        result.SheetName = sheet.SheetName;

        // ---- 日番号行の特定 ----
        var header = ExcelHelper.FindDayNumberRow(sheet, scanRows: 20, scanCols: 40, minRun: 5);
        if (header == null)
        {
            result.Messages.Add("[E-PU-002] 日番号(1,2,3...)が並ぶ行を検出できません。出席記録シートを選択しているか確認してください。");
            return result;
        }
        result.DayHeaderRow = header.RowIndex;
        result.DayStartColumn = header.StartColumn;
        result.DayCount = header.DayCount;

        // ---- 対象年月の決定 ----
        var detected = DetectPeriodStart(sheet, header.RowIndex);
        int year, month;
        if (overrideYearMonth is { } ov) { year = ov.Year; month = ov.Month; }
        else if (detected is { } d) { year = d.Year; month = d.Month; }
        else
        {
            result.Messages.Add("[E-PU-003] 対象年月を判定できません。画面で対象年月を指定してください。");
            return result;
        }
        result.Year = year;
        result.Month = month;
        result.PeriodStart = detected;
        int daysInMonth = DateTime.DaysInMonth(year, month);

        // ---- 社員ブロックの位置を洗い出す ----
        var metaRows = new List<int>();
        for (int r = header.RowIndex + 1; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;
            if (ExcelHelper.Text(row, 0).Contains("作業番号")) metaRows.Add(r);
        }
        if (metaRows.Count == 0)
        {
            result.Messages.Add("[E-PU-004] 社員ブロック(『作業番号:』の行)を検出できません。");
            return result;
        }

        for (int i = 0; i < metaRows.Count; i++)
        {
            int metaRow = metaRows[i];
            int blockEnd = (i + 1 < metaRows.Count ? metaRows[i + 1] : sheet.LastRowNum + 1) - 1;
            var meta = sheet.GetRow(metaRow)!;

            string employeeNo = ValueAfterLabel(meta, "作業番号");
            string name = ValueAfterLabel(meta, "名前");
            string department = ValueAfterLabel(meta, "部門");
            if (name.Length == 0)
            {
                result.Messages.Add($"[W-PU-005] {ExcelHelper.CellRef(sheet, metaRow, 0)} の社員ブロックに氏名がありません。");
                continue;
            }

            // 打刻データ側の氏名を正式氏名として登録する(作業番号を持つため信頼できる)
            _alias.RegisterCanonical(name);
            var person = _normalizer.Resolve(name, department, employeeNo);
            result.EmployeeCount++;

            // 帳票の並び順・氏名・部門はこの名簿を基準にする(お客様の出席記録は打刻データの複製のため)
            if (!result.Roster.Any(e => e.Key == person.Key))
            {
                result.Roster.Add(new PunchRosterEntry
                {
                    Key = person.Key,
                    DisplayName = name,
                    Department = department,
                    EmployeeNo = employeeNo,
                    Order = result.Roster.Count,
                    SourceRow = metaRow
                });
            }

            // ブロック内の行数で打刻行を決める。
            //   2行構成(メタ+打刻)  : メタ+1 が打刻行
            //   3行構成(メタ+シフト+打刻): メタ+2 が打刻行
            int blockRowCount = blockEnd - metaRow;
            int punchRow = blockRowCount >= 2 ? metaRow + 2 : metaRow + 1;
            if (punchRow > sheet.LastRowNum) continue;
            result.BlockRowCount = blockRowCount + 1;

            var prow = sheet.GetRow(punchRow);
            if (prow == null) continue;

            for (int d = 0; d < header.DayCount && d < daysInMonth; d++)
            {
                int col = header.StartColumn + d;
                var text = ExcelHelper.Text(prow, col);
                if (text.Length == 0) continue;

                var times = ExtractTimes(text);
                var date = new DateOnly(year, month, d + 1);

                result.Punches.Add(new PunchDaily
                {
                    Person = person,
                    WorkDate = date,
                    RawValue = text,
                    Times = times,
                    SourceCell = ExcelHelper.CellRef(sheet, punchRow, col)
                });

                if (times.Count == 0)
                    result.Messages.Add($"[W-PU-006] {ExcelHelper.CellRef(sheet, punchRow, col)} から時刻を抽出できません: '{text}'");
            }
        }

        if (result.Punches.Count == 0)
            result.Messages.Add("[E-PU-007] 打刻データを1件も読み取れませんでした。");

        return result;
    }

    /// <summary>"06:4917:07" や "06:49\n17:07" から全ての時刻を取り出す。</summary>
    public static IReadOnlyList<TimeSpan> ExtractTimes(string text)
    {
        var list = new List<TimeSpan>();
        foreach (Match m in TimePattern.Matches(text))
        {
            int h = int.Parse(m.Groups[1].Value);
            int mi = int.Parse(m.Groups[2].Value);
            if (h > 47 || mi > 59) continue;      // 日跨ぎを考慮して 47時まで許容
            list.Add(new TimeSpan(h, mi, 0));
        }
        return list;
    }

    /// <summary>"名前:" のようなラベルを行内から探し、その右隣で最初に値が入るセルを返す。</summary>
    private static string ValueAfterLabel(IRow row, string label)
    {
        int last = row.LastCellNum;
        for (int c = 0; c < last; c++)
        {
            var text = ExcelHelper.Text(row, c);
            if (!text.Contains(label)) continue;
            for (int k = c + 1; k < last && k <= c + 6; k++)
            {
                var v = ExcelHelper.Text(row, k);
                if (v.Length > 0) return v;
            }
        }
        return string.Empty;
    }

    /// <summary>ヘッダ付近の "2026-07-01 ~ 2026-07-07" から期間開始日を読む。</summary>
    private static DateOnly? DetectPeriodStart(ISheet sheet, int dayHeaderRow)
    {
        for (int r = 0; r <= dayHeaderRow; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;
            int last = Math.Max((int)row.LastCellNum, 1);
            for (int c = 0; c < last; c++)
            {
                var text = ExcelHelper.Text(row, c);
                if (text.Length < 8) continue;
                var m = IsoDatePattern.Match(text);
                if (!m.Success) continue;
                int y = int.Parse(m.Groups[1].Value);
                int mo = int.Parse(m.Groups[2].Value);
                int dd = int.Parse(m.Groups[3].Value);
                if (y is >= 2000 and <= 2100 && mo is >= 1 and <= 12 && dd is >= 1 and <= 31)
                    return new DateOnly(y, mo, dd);
            }
        }
        return null;
    }
}

/// <summary>打刻解析の結果と、解析に使った位置情報。</summary>
public sealed class PunchParseResult
{
    public List<PunchDaily> Punches { get; } = new();
    public List<string> Messages { get; } = new();
    public string SheetName { get; set; } = string.Empty;
    public int Year { get; set; }
    public int Month { get; set; }
    public DateOnly? PeriodStart { get; set; }
    public int DayHeaderRow { get; set; } = -1;
    public int DayStartColumn { get; set; } = -1;
    public int DayCount { get; set; }
    public int EmployeeCount { get; set; }
    public int BlockRowCount { get; set; }
    /// <summary>打刻データに載っている社員(記載順)。帳票の並び順・氏名・部門の基準にする。</summary>
    public List<PunchRosterEntry> Roster { get; } = new();

    public string LayoutSummary =>
        DayHeaderRow < 0
            ? "レイアウト未検出"
            : $"シート={SheetName} 対象={Year}年{Month}月 日番号行={DayHeaderRow + 1} " +
              $"日列={ExcelHelper.ColumnName(DayStartColumn)}〜{ExcelHelper.ColumnName(DayStartColumn + DayCount - 1)}({DayCount}日分) " +
              $"社員数={EmployeeCount} ブロック={BlockRowCount}行構成";
}
