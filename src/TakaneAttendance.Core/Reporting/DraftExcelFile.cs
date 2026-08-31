using System.Globalization;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Reporting;

/// <summary>
/// 一時保存ファイル(統合仕様書 v3.0 第8.4章)。
///
/// 仕様書の指定どおり Excel 形式で保存する。ファイル名は
///   勤怠突合状況_YYYYMM_YYYYMMDD_HHMMSS.xlsx
///
/// 3つのシートに分けて持つ。
///   [入力条件] 取り込んだファイル・対象年月・絞り込み設定・入力原本のハッシュ
///   [帳票]     社員ごとに シフト / 打刻 / 予定終了 / 備考 / 編集 の5行 × 日列
///   [修正履歴] 修正ID・日時・修正者・前後の値・判定の変化
///
/// 判定は保存値をそのまま使わず、開いたときに突合と同じルールで計算し直す。
/// 保留中にマスタを直した場合、その内容が反映された状態で再開できる。
///
/// 入力原本のハッシュを持つのは、保留後に元ファイルが差し替わっていないかを
/// 再開時に確かめるため(仕様書 8.4「入力原本の変更有無を検証」)。
/// </summary>
public static class DraftExcelFile
{
    public const int CurrentFormatVersion = 2;

    private const string SheetInputs = "入力条件";
    private const string SheetReport = "帳票";
    private const string SheetHistory = "修正履歴";

    // [帳票] シートの行区分
    private static readonly string[] RowKinds = { "シフト", "打刻", "予定終了", "備考", "編集", "打刻編集" };

    /// <summary>
    /// 一時保存の置き場所(仕様書 v3.0 第4.3章「作業データ」)。
    /// 実行ファイルと同じ階層に置き、masters と同じ扱いにする。
    /// </summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "作業データ");

    /// <summary>
    /// 一時保存ファイルの決まった置き場所。
    ///
    /// 作業の続きは1つしか持たないため、ファイルは1つに固定して毎回上書きする。
    /// 保存のたびに保存先を聞かない(締めの作業中に何度も保存するため)。
    /// 上書き前の内容は <see cref="BackupPath"/> に残す。
    /// </summary>
    public static string DefaultPath => Path.Combine(DefaultDirectory, "勤怠突合状況.xlsx");

    /// <summary>上書き前の控え。直前の1世代だけ残す。</summary>
    public static string BackupPath => DefaultPath + ".bak";

    /// <summary>仕様書 8.4 の命名規則にそったファイル名(控えを別名で残す場合に使う)。</summary>
    public static string SuggestFileName(int year, int month, DateTime now)
        => $"勤怠突合状況_{year}{month:00}_{now:yyyyMMdd_HHmmss}.xlsx";

    /// <summary>一時保存ファイルの対象年月を読む(上書きの確認に使う)。無ければ null。</summary>
    public static (int Year, int Month)? PeekPeriod(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            using var wb = ExcelHelper.OpenWorkbook(path);
            var sheet = wb.GetSheet(SheetInputs);
            if (sheet == null) return null;

            var values = ReadKeyValues(sheet);
            int year = ReadInt(values, "対象年", 0);
            int month = ReadInt(values, "対象月", 0);
            return year > 0 && month > 0 ? (year, month) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Save(string path, ReportSheet sheet, DraftInputs inputs)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        // 上書き前の内容を1世代だけ残す(誤って別の月を保存したときに戻せるように)
        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);

        using var wb = new XSSFWorkbook();
        WriteInputs(wb, sheet, inputs);
        WriteReport(wb, sheet);
        WriteHistory(wb, sheet);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        wb.Write(fs);
    }

    // ================= 保存 =================

    private static void WriteInputs(IWorkbook wb, ReportSheet sheet, DraftInputs inputs)
    {
        var s = wb.CreateSheet(SheetInputs);
        int r = 0;

        void Put(string key, string value)
        {
            var row = s.CreateRow(r++);
            row.CreateCell(0).SetCellValue(key);
            row.CreateCell(1).SetCellValue(value);
        }

        Put("形式の版数", CurrentFormatVersion.ToString());
        Put("保存日時", DateTime.Now.ToString("yyyy/MM/dd HH:mm:ss"));
        Put("保存者", Environment.UserName);
        Put("対象年", sheet.Year.ToString());
        Put("対象月", sheet.Month.ToString());
        Put("日数", sheet.DayCount.ToString());
        Put("シフト表", inputs.ShiftPath);
        Put("シフト表シート", inputs.ShiftSheetName ?? "");
        Put("打刻データ", inputs.PunchPath);
        Put("打刻データシート", inputs.PunchSheetName ?? "");
        Put("テンプレート", inputs.TemplatePath);
        Put("マスタフォルダ", inputs.MastersDirectory ?? "");
        Put("シフト表に載っている社員のみ", inputs.OnlyPersonsInShift ? "はい" : "いいえ");
        Put("シフト表ハッシュ", HashOf(inputs.ShiftPath));
        Put("打刻データハッシュ", HashOf(inputs.PunchPath));

        s.SetColumnWidth(0, 34 * 256);
        s.SetColumnWidth(1, 80 * 256);
    }

    private static void WriteReport(IWorkbook wb, ReportSheet sheet)
    {
        var s = wb.CreateSheet(SheetReport);
        int r = 0;

        var header = s.CreateRow(r++);
        var labels = new[] { "社員番号", "氏名", "部門", "突合キー", "区分" };
        for (int c = 0; c < labels.Length; c++) header.CreateCell(c).SetCellValue(labels[c]);
        for (int d = 1; d <= sheet.DayCount; d++) header.CreateCell(labels.Length + d - 1).SetCellValue(d);

        foreach (var block in sheet.Employees)
        {
            foreach (var kind in RowKinds)
            {
                var row = s.CreateRow(r++);
                row.CreateCell(0).SetCellValue(block.EmployeeNo);
                row.CreateCell(1).SetCellValue(block.Name);
                row.CreateCell(2).SetCellValue(block.Department);
                row.CreateCell(3).SetCellValue(block.Key);
                row.CreateCell(4).SetCellValue(kind);

                for (int i = 0; i < sheet.DayCount; i++)
                {
                    var value = kind switch
                    {
                        "シフト" => block.Shift[i],
                        "打刻" => block.Punch[i],
                        "予定終了" => block.PlannedEnd[i],
                        "備考" => block.Note[i],
                        "打刻編集" => block.PunchEdited[i] ? "編集" : "",
                        _ => block.ShiftEdited[i] ? "編集" : ""
                    };
                    if (value.Length > 0) row.CreateCell(5 + i).SetCellValue(value);
                }
            }
        }

        s.CreateFreezePane(5, 1);
        for (int c = 0; c < 5; c++) s.SetColumnWidth(c, 16 * 256);
    }

    private static void WriteHistory(IWorkbook wb, ReportSheet sheet)
    {
        var s = wb.CreateSheet(SheetHistory);
        int r = 0;

        var header = s.CreateRow(r++);
        var labels = new[] { "修正ID", "修正日時", "修正者", "社員番号", "氏名", "日付",
                             "項目", "修正前", "修正後", "修正前の判定", "修正後の判定", "備考" };
        for (int c = 0; c < labels.Length; c++) header.CreateCell(c).SetCellValue(labels[c]);

        foreach (var e in sheet.History.Entries)
        {
            var row = s.CreateRow(r++);
            row.CreateCell(0).SetCellValue(e.Id);
            row.CreateCell(1).SetCellValue(e.EditedAt.ToString("yyyy/MM/dd HH:mm:ss"));
            row.CreateCell(2).SetCellValue(e.EditedBy);
            row.CreateCell(3).SetCellValue(e.EmployeeNo);
            row.CreateCell(4).SetCellValue(e.PersonName);
            row.CreateCell(5).SetCellValue(e.WorkDate.ToString("yyyy/MM/dd"));
            row.CreateCell(6).SetCellValue(e.Field);
            row.CreateCell(7).SetCellValue(e.Before);
            row.CreateCell(8).SetCellValue(e.After);
            row.CreateCell(9).SetCellValue(e.JudgementBefore);
            row.CreateCell(10).SetCellValue(e.JudgementAfter);
            row.CreateCell(11).SetCellValue(e.Note);
        }

        int[] widths = { 8, 20, 14, 10, 16, 12, 16, 18, 18, 12, 12, 30 };
        for (int c = 0; c < widths.Length; c++) s.SetColumnWidth(c, widths[c] * 256);
        s.CreateFreezePane(0, 1);
    }

    // ================= 復元 =================

    /// <summary>読み込み結果。</summary>
    public sealed class LoadResult
    {
        public required ReportSheet Sheet { get; init; }
        public required DraftInputs Inputs { get; init; }
        public List<string> Messages { get; } = new();
        /// <summary>保留したときと判定が変わったセル数</summary>
        public int ChangedJudgements { get; set; }
    }

    /// <param name="judge">判定器。null なら判定を計算し直さない。</param>
    public static LoadResult Load(string path, ReportJudge? judge)
    {
        using var wb = ExcelHelper.OpenWorkbook(path);

        var inputSheet = wb.GetSheet(SheetInputs) ?? throw new InvalidOperationException(
            $"この Excel は一時保存ファイルではありません(シート「{SheetInputs}」がありません)。");
        var values = ReadKeyValues(inputSheet);

        int version = ReadInt(values, "形式の版数", 1);
        var sheet = new ReportSheet
        {
            Year = ReadInt(values, "対象年", 0),
            Month = ReadInt(values, "対象月", 0),
            DayCount = ReadInt(values, "日数", 0)
        };

        var inputs = new DraftInputs
        {
            ShiftPath = Get(values, "シフト表"),
            ShiftSheetName = Get(values, "シフト表シート"),
            PunchPath = Get(values, "打刻データ"),
            PunchSheetName = Get(values, "打刻データシート"),
            TemplatePath = Get(values, "テンプレート"),
            MastersDirectory = Get(values, "マスタフォルダ"),
            AutoDetectYearMonth = true,
            OnlyPersonsInShift = Get(values, "シフト表に載っている社員のみ") == "はい"
        };

        var result = new LoadResult { Sheet = sheet, Inputs = inputs };

        if (version > CurrentFormatVersion)
            result.Messages.Add($"この一時保存ファイルは新しい形式(第{version}版)です。" +
                                $"このアプリは第{CurrentFormatVersion}版まで読めます。アプリを更新してください。");

        // 入力原本が差し替わっていないかを確かめる(仕様書 8.4)
        CheckHash(result, "シフト表", inputs.ShiftPath, Get(values, "シフト表ハッシュ"));
        CheckHash(result, "打刻データ", inputs.PunchPath, Get(values, "打刻データハッシュ"));

        ReadReport(wb, sheet, result, judge);
        ReadHistory(wb, sheet);

        result.Messages.Add($"保存日時 {Get(values, "保存日時")} / 保存者 {Get(values, "保存者")}");
        return result;
    }

    private static void ReadReport(IWorkbook wb, ReportSheet sheet, LoadResult result, ReportJudge? judge)
    {
        var s = wb.GetSheet(SheetReport);
        if (s == null)
        {
            result.Messages.Add($"シート「{SheetReport}」がありません。帳票の内容を復元できませんでした。");
            return;
        }

        ReportEmployeeBlock? block = null;
        string currentKey = "";

        for (int r = 1; r <= s.LastRowNum; r++)
        {
            var row = s.GetRow(r);
            if (row == null) continue;

            var key = ExcelHelper.Text(row.GetCell(3));
            var kind = ExcelHelper.Text(row.GetCell(4));
            if (kind.Length == 0) continue;

            if (block == null || key != currentKey)
            {
                block = new ReportEmployeeBlock(sheet.DayCount)
                {
                    EmployeeNo = ExcelHelper.Text(row.GetCell(0)),
                    Name = ExcelHelper.Text(row.GetCell(1)),
                    Department = ExcelHelper.Text(row.GetCell(2)),
                    Key = key,
                    HasMatchingData = true
                };
                sheet.Employees.Add(block);
                currentKey = key;
            }

            for (int i = 0; i < sheet.DayCount; i++)
            {
                var value = ExcelHelper.Text(row.GetCell(5 + i));
                switch (kind)
                {
                    case "シフト": block.Shift[i] = value; break;
                    case "打刻": block.Punch[i] = value; break;
                    case "予定終了": block.PlannedEnd[i] = value; break;
                    case "備考": block.Note[i] = value; break;
                    case "編集": block.ShiftEdited[i] = value.Length > 0; break;
                    case "打刻編集": block.PunchEdited[i] = value.Length > 0; break;
                }
            }
        }

        // 判定は開いたときのマスタで計算し直す
        if (judge == null || sheet.Year == 0) return;

        foreach (var b in sheet.Employees)
        {
            if (b.Person == null) continue;
            for (int i = 0; i < sheet.DayCount; i++)
            {
                var before = b.Judgements[i];
                b.Judgements[i] = judge.Evaluate(b.Person, new DateOnly(sheet.Year, sheet.Month, i + 1),
                                                 b.Shift[i], b.Punch[i], b.PlannedEnd[i]);
                if (b.Judgements[i].Judgement != before.Judgement) result.ChangedJudgements++;
            }
        }
    }

    private static void ReadHistory(IWorkbook wb, ReportSheet sheet)
    {
        var s = wb.GetSheet(SheetHistory);
        if (s == null) return;

        var entries = new List<EditHistoryEntry>();
        for (int r = 1; r <= s.LastRowNum; r++)
        {
            var row = s.GetRow(r);
            if (row == null) continue;

            var idText = ExcelHelper.Text(row.GetCell(0));
            if (!int.TryParse(idText, out var id)) continue;

            entries.Add(new EditHistoryEntry
            {
                Id = id,
                EditedAt = DateTime.TryParse(ExcelHelper.Text(row.GetCell(1)), out var at) ? at : DateTime.Now,
                EditedBy = ExcelHelper.Text(row.GetCell(2)),
                EmployeeNo = ExcelHelper.Text(row.GetCell(3)),
                PersonName = ExcelHelper.Text(row.GetCell(4)),
                WorkDate = DateOnly.TryParse(ExcelHelper.Text(row.GetCell(5)), out var d) ? d : default,
                Field = ExcelHelper.Text(row.GetCell(6)),
                Before = ExcelHelper.Text(row.GetCell(7)),
                After = ExcelHelper.Text(row.GetCell(8)),
                JudgementBefore = ExcelHelper.Text(row.GetCell(9)),
                JudgementAfter = ExcelHelper.Text(row.GetCell(10)),
                Note = ExcelHelper.Text(row.GetCell(11))
            });
        }
        sheet.History.Restore(entries);
    }

    // ================= 補助 =================

    private static void CheckHash(LoadResult result, string role, string path, string savedHash)
    {
        if (savedHash.Length == 0 || path.Length == 0) return;
        if (!File.Exists(path))
        {
            result.Messages.Add($"{role}のファイルが見つかりません({path})。");
            return;
        }
        if (HashOf(path) != savedHash)
            result.Messages.Add($"{role}が保留したときから差し替わっています({path})。");
    }

    private static string HashOf(string path) => LogInputFile.From("", path).Hash;

    private static Dictionary<string, string> ReadKeyValues(ISheet sheet)
    {
        var map = new Dictionary<string, string>();
        for (int r = 0; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;
            var key = ExcelHelper.Text(row.GetCell(0));
            if (key.Length > 0) map[key] = ExcelHelper.Text(row.GetCell(1));
        }
        return map;
    }

    private static string Get(IReadOnlyDictionary<string, string> map, string key)
        => map.TryGetValue(key, out var v) ? v : "";

    private static int ReadInt(IReadOnlyDictionary<string, string> map, string key, int fallback)
        => int.TryParse(Get(map, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : fallback;
}
