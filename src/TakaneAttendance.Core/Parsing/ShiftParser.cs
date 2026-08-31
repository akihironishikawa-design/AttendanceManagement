using System.Text.RegularExpressions;
using NPOI.SS.UserModel;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Naming;

namespace TakaneAttendance.Core.Parsing;

/// <summary>
/// シフト表(.xls)の解析。
///
/// 実サンプル「2026 営業課・競技課シフト(原本行事)表.xls」の "7月修正" シートで確認した構造:
///   B29 : 対象期間 "2026/7/1～2025/7/31"
///   G31:AK31 : 日番号 1..31
///   D32 : 曜日ラベル / D33以降 : 氏名
///   B列 : 部門(グループ先頭行のみ。以降は上の値を引き継ぐ)
///   G:AK : 日別の値。時刻セル = 予定開始時刻、文字セル = 勤務区分(公・有・欠・出張 など)
///
/// 日番号行は決め打ちせず「1,2,3... と連番が並ぶ行」を探して特定するため、
/// 月やシートによって行位置がずれても動作する。
/// </summary>
public sealed class ShiftParser
{
    private static readonly Regex PeriodPattern =
        new(@"(\d{4})\s*[/\-年]\s*(\d{1,2})\s*[/\-月]\s*(\d{1,2})", RegexOptions.Compiled);

    /// <summary>氏名列に現れるが社員ではないラベル。</summary>
    private static readonly HashSet<string> NonEmployeeLabels = new()
    {
        "曜日", "予約組数", "シフト№", "シフトNo", "部門", "行事", "氏名",
        "Total", "SubTotal", "Sub Total", "合計", "小計", "休日数"
    };

    private readonly NameNormalizer _normalizer;
    private readonly ShiftTypeMaster _shiftTypes;

    public ShiftParser(NameNormalizer normalizer, ShiftTypeMaster shiftTypes)
    {
        _normalizer = normalizer;
        _shiftTypes = shiftTypes;
    }

    /// <summary>
    /// シフトシートを1人1日に展開する。
    /// </summary>
    /// <param name="sheetName">対象シート名。null なら先頭シート。</param>
    /// <param name="overrideYearMonth">対象年月を明示指定する場合。null ならシートから読み取る。</param>
    public ShiftParseResult Parse(string path, string? sheetName, (int Year, int Month)? overrideYearMonth = null)
    {
        var result = new ShiftParseResult();
        using var wb = ExcelHelper.OpenWorkbook(path);

        var sheet = sheetName != null ? wb.GetSheet(sheetName) : wb.GetSheetAt(0);
        if (sheet == null)
        {
            result.Messages.Add($"[E-SH-001] シート '{sheetName}' が見つかりません。");
            return result;
        }
        result.SheetName = sheet.SheetName;

        // ---- 日番号行の特定 ----
        var header = ExcelHelper.FindDayNumberRow(sheet, scanRows: 60, scanCols: 60, minRun: 10);
        if (header == null)
        {
            result.Messages.Add("[E-SH-002] 日番号(1,2,3...)が並ぶ行を検出できません。シフト表の形式を確認してください。");
            return result;
        }
        result.DayHeaderRow = header.RowIndex;
        result.DayStartColumn = header.StartColumn;
        result.DayCount = header.DayCount;

        // ---- 対象年月の決定 ----
        var (year, month) = overrideYearMonth ?? DetectYearMonth(sheet, header.RowIndex) ?? (0, 0);
        if (year == 0)
        {
            result.Messages.Add("[E-SH-003] 対象年月を判定できません。画面で対象年月を指定してください。");
            return result;
        }
        result.Year = year;
        result.Month = month;
        int daysInMonth = DateTime.DaysInMonth(year, month);

        // ---- 氏名列の推定(日番号行の左側で、最も氏名らしい値が多い列) ----
        int nameCol = DetectNameColumn(sheet, header);
        result.NameColumn = nameCol;

        // ---- 社員行の走査 ----
        string currentDepartment = string.Empty;
        for (int r = header.RowIndex + 1; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            // 部門は左側の列にグループ先頭のみ入るため、値があれば引き継ぐ
            for (int c = 0; c < nameCol; c++)
            {
                var v = ExcelHelper.Text(row, c);
                if (v.Length > 0 && !NonEmployeeLabels.Contains(v) && !v.StartsWith("Sub", StringComparison.OrdinalIgnoreCase))
                    currentDepartment = v;
            }

            var name = ExcelHelper.Text(row, nameCol);
            if (name.Length == 0) continue;
            if (NonEmployeeLabels.Contains(name)) continue;
            if (name.StartsWith("Sub", StringComparison.OrdinalIgnoreCase)) continue;
            if (name.Equals("Total", StringComparison.OrdinalIgnoreCase)) continue;

            var person = _normalizer.Resolve(name, currentDepartment);
            int dailyCount = 0;
            int rosterOrder = result.Roster.Count;

            for (int d = 0; d < header.DayCount && d < daysInMonth; d++)
            {
                int col = header.StartColumn + d;
                var cell = row.GetCell(col);
                if (cell == null) continue;

                var date = new DateOnly(year, month, d + 1);
                var time = ExcelHelper.TimeOfDay(cell);
                var text = ExcelHelper.Text(cell);
                if (text.Length == 0) continue;

                ShiftDaily shift;
                if (time.HasValue)
                {
                    // 時刻セル = 通常勤務の予定開始時刻
                    shift = new ShiftDaily
                    {
                        Person = person,
                        WorkDate = date,
                        RawValue = text,
                        Kind = ShiftKind.Work,
                        PlannedStart = time,
                        SourceCell = ExcelHelper.CellRef(sheet, r, col)
                    };
                }
                else
                {
                    var kind = _shiftTypes.Resolve(text);
                    shift = new ShiftDaily
                    {
                        Person = person,
                        WorkDate = date,
                        RawValue = text,
                        Kind = kind,
                        ShiftTypeCode = text,
                        SourceCell = ExcelHelper.CellRef(sheet, r, col)
                    };
                    if (kind == ShiftKind.Unknown) result.UnknownShiftValues.Add(text);
                }

                result.Shifts.Add(shift);
                dailyCount++;
            }

            if (dailyCount == 0) continue;

            result.EmployeeCount++;
            // 帳票の社員一覧・並び順は、このシフト表の記載順をそのまま使う
            result.Roster.Add(new ShiftRosterEntry
            {
                Key = person.Key,
                SourceName = person.SourceName,
                DisplayName = person.DisplayName,
                Department = person.Department ?? "",
                Order = rosterOrder,
                SourceRow = r
            });
        }

        if (result.Shifts.Count == 0)
            result.Messages.Add("[E-SH-004] 対象シートから勤務予定を1件も読み取れませんでした。シート選択を確認してください。");

        return result;
    }

    /// <summary>日番号行より上にある「yyyy/M/d」形式の文字列から対象年月を判定する。</summary>
    private static (int Year, int Month)? DetectYearMonth(ISheet sheet, int dayHeaderRow)
    {
        for (int r = Math.Max(0, dayHeaderRow - 8); r <= dayHeaderRow; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;
            for (int c = 0; c <= 12; c++)
            {
                var text = ExcelHelper.Text(row, c);
                if (text.Length < 6) continue;
                var m = PeriodPattern.Match(text);
                if (!m.Success) continue;
                int y = int.Parse(m.Groups[1].Value);
                int mo = int.Parse(m.Groups[2].Value);
                if (y is >= 2000 and <= 2100 && mo is >= 1 and <= 12) return (y, mo);
            }
        }
        return null;
    }

    /// <summary>
    /// 日番号列より左の列のうち、日本語氏名らしい文字列が最も多く入る列を氏名列とみなす。
    /// レイアウト変更に強くするため列位置を決め打ちしない。
    /// </summary>
    private static int DetectNameColumn(ISheet sheet, DayHeader header)
    {
        int bestCol = 3, bestScore = -1;
        int scanTo = Math.Min(sheet.LastRowNum, header.RowIndex + 60);

        for (int c = 0; c < header.StartColumn; c++)
        {
            int score = 0;
            for (int r = header.RowIndex + 1; r <= scanTo; r++)
            {
                var text = ExcelHelper.Text(sheet.GetRow(r), c);
                if (text.Length is < 2 or > 20) continue;
                if (NonEmployeeLabels.Contains(text)) continue;
                if (text.StartsWith("Sub", StringComparison.OrdinalIgnoreCase)) continue;
                // 数字だけの列(組数など)は氏名ではない
                if (text.All(char.IsDigit)) continue;
                score++;
            }
            if (score > bestScore) { bestScore = score; bestCol = c; }
        }
        return bestCol;
    }
}

/// <summary>シフト解析の結果と、解析に使った位置情報。</summary>
public sealed class ShiftParseResult
{
    public List<ShiftDaily> Shifts { get; } = new();
    /// <summary>シフト表に載っている社員(記載順)</summary>
    public List<ShiftRosterEntry> Roster { get; } = new();
    public List<string> Messages { get; } = new();
    public HashSet<string> UnknownShiftValues { get; } = new();
    public string SheetName { get; set; } = string.Empty;
    public int Year { get; set; }
    public int Month { get; set; }
    public int DayHeaderRow { get; set; } = -1;
    public int DayStartColumn { get; set; } = -1;
    public int DayCount { get; set; }
    public int NameColumn { get; set; } = -1;
    public int EmployeeCount { get; set; }

    public string LayoutSummary =>
        DayHeaderRow < 0
            ? "レイアウト未検出"
            : $"シート={SheetName} 対象={Year}年{Month}月 日番号行={DayHeaderRow + 1} " +
              $"日列={ExcelHelper.ColumnName(DayStartColumn)}〜{ExcelHelper.ColumnName(DayStartColumn + DayCount - 1)} " +
              $"氏名列={ExcelHelper.ColumnName(NameColumn)} 社員数={EmployeeCount}";
}
