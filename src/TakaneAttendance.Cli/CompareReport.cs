using NPOI.SS.UserModel;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Naming;
using TakaneAttendance.Core.Parsing;

namespace TakaneAttendance.Cli;

/// <summary>
/// 出席記録レポートの突き合わせ。
///
/// お客様が手作業で作られた出席記録(タイムレコーダー出力ブックの「出席記録 (2)」シート)と、
/// 本システムが出力した出席記録レポートを、社員 × 日で比べて差分を出す。
///
/// 31日 × 数十名を目視で確かめるのは現実的でないため、受入確認の道具として用意する。
/// どちらのシートも「日番号行 → 曜日行 → 社員ブロック(作業番号:/名前:/部門: → シフト行 → 打刻行)」
/// という同じ作りだが、手作業側はブロックの行数が2〜4行と揃っていないため、行の中身で種別を判断する。
/// </summary>
internal static class CompareReport
{
    public static int Run(string expectedPath, string expectedSheet, string actualPath, string actualSheet, int limit)
    {
        var expected = ReadSheet(expectedPath, expectedSheet);
        var actual = ReadSheet(actualPath, actualSheet);

        Console.WriteLine($"期待 : {Path.GetFileName(expectedPath)} [{expectedSheet}]  {expected.Count} 名");
        Console.WriteLine($"実際 : {Path.GetFileName(actualPath)} [{actualSheet}]  {actual.Count} 名");
        Console.WriteLine();

        var onlyExpected = expected.Keys.Where(k => !actual.ContainsKey(k)).ToList();
        var onlyActual = actual.Keys.Where(k => !expected.ContainsKey(k)).ToList();
        var both = expected.Keys.Where(actual.ContainsKey).ToList();

        Console.WriteLine("================ 社員の突き合わせ ================");
        Console.WriteLine($"  両方にいる : {both.Count} 名");
        Console.WriteLine($"  期待のみ   : {onlyExpected.Count} 名");
        foreach (var k in onlyExpected.Take(limit)) Console.WriteLine($"      {expected[k].Name}");
        if (onlyExpected.Count > limit) Console.WriteLine($"      ...ほか {onlyExpected.Count - limit} 名");
        Console.WriteLine($"  実際のみ   : {onlyActual.Count} 名");
        foreach (var k in onlyActual.Take(limit)) Console.WriteLine($"      {actual[k].Name}");
        if (onlyActual.Count > limit) Console.WriteLine($"      ...ほか {onlyActual.Count - limit} 名");
        Console.WriteLine();

        // 並び順は「両方にいる社員だけ」を取り出して比べる(片方にしかいない社員でずれるのを避ける)
        var expectedOrder = expected.Values.Where(e => actual.ContainsKey(e.Key)).Select(e => e.Key).ToList();
        var actualOrder = actual.Values.Where(e => expected.ContainsKey(e.Key)).Select(e => e.Key).ToList();
        Console.WriteLine("================ 並び順 ================");
        if (expectedOrder.SequenceEqual(actualOrder))
        {
            Console.WriteLine("  一致");
        }
        else
        {
            Console.WriteLine("  不一致");
            for (int i = 0; i < Math.Max(expectedOrder.Count, actualOrder.Count) && i < limit; i++)
            {
                var e = i < expectedOrder.Count ? expected[expectedOrder[i]].Name : "(なし)";
                var a = i < actualOrder.Count ? actual[actualOrder[i]].Name : "(なし)";
                Console.WriteLine($"  {i + 1,3}  期待 {Pad(e, 16)} / 実際 {a}{(e == a ? "" : "   ←ずれ")}");
            }
        }
        Console.WriteLine();

        Console.WriteLine("================ 中身の突き合わせ ================");
        int diffTotal = 0, cellTotal = 0, personDiff = 0;

        foreach (var key in expectedOrder)
        {
            var e = expected[key];
            var a = actual[key];
            var lines = new List<string>();

            if (e.EmployeeNo != a.EmployeeNo) lines.Add($"    作業番号  期待『{e.EmployeeNo}』 / 実際『{a.EmployeeNo}』");
            if (e.Name != a.Name) lines.Add($"    名前      期待『{e.Name}』 / 実際『{a.Name}』");
            if (e.Department != a.Department) lines.Add($"    部門      期待『{e.Department}』 / 実際『{a.Department}』");

            foreach (var day in e.Days.Keys.Union(a.Days.Keys).OrderBy(d => d))
            {
                var ed = e.Days.TryGetValue(day, out var x) ? x : DayValues.Empty;
                var ad = a.Days.TryGetValue(day, out var y) ? y : DayValues.Empty;
                cellTotal += 2;

                if (ed.Shift != ad.Shift)
                {
                    lines.Add($"    {day,2}日 シフト  期待『{ed.Shift}』 / 実際『{ad.Shift}』");
                    diffTotal++;
                }
                if (Normalize(ed.Punch) != Normalize(ad.Punch))
                {
                    lines.Add($"    {day,2}日 打刻    期待『{ed.Punch}』 / 実際『{ad.Punch}』");
                    diffTotal++;
                }
            }

            if (e.Note.Count > 0)
                lines.Add("    備考行(期待のみ) : " + string.Join(" , ", e.Note.Select(n => $"{n.Key}日={n.Value}")));

            if (lines.Count == 0) continue;
            personDiff++;
            Console.WriteLine($"  ● {e.Name} (作業番号 {e.EmployeeNo})");
            foreach (var line in lines.Take(limit)) Console.WriteLine(line);
            if (lines.Count > limit) Console.WriteLine($"    ...ほか {lines.Count - limit} 件");
        }

        Console.WriteLine();
        Console.WriteLine("================ まとめ ================");
        Console.WriteLine($"  比較したセル : {cellTotal} 件 (両方にいる {expectedOrder.Count} 名 × 日 × シフト/打刻)");
        Console.WriteLine($"  値の違い     : {diffTotal} 件 / 差分のある社員 {personDiff} 名");
        Console.WriteLine(diffTotal == 0 && onlyExpected.Count == 0 && onlyActual.Count == 0
            ? "  → 完全一致"
            : "  → 差分あり");
        return 0;
    }

    /// <summary>打刻の比較は、セル内改行と空白の違いを無視する(手作業側は改行で2段にした箇所がある)。</summary>
    private static string Normalize(string text)
        => text.Replace("\n", "").Replace("\r", "").Replace(" ", "").Replace("　", "");

    private static string Pad(string s, int width)
    {
        int w = s.Sum(c => c < 0x80 ? 1 : 2);
        return s + new string(' ', Math.Max(0, width - w));
    }

    // ---- 読み取り --------------------------------------------------------

    private sealed record DayValues(string Shift, string Punch)
    {
        public static readonly DayValues Empty = new("", "");
    }

    private sealed class Block
    {
        public required string Key { get; init; }
        public required string Name { get; init; }
        public required string EmployeeNo { get; init; }
        public required string Department { get; init; }
        public Dictionary<int, DayValues> Days { get; } = new();
        public Dictionary<int, string> Note { get; } = new();
    }

    private static Dictionary<string, Block> ReadSheet(string path, string sheetName)
    {
        using var wb = ExcelHelper.OpenWorkbook(path);
        var sheet = wb.GetSheet(sheetName) ?? throw new ArgumentException($"シート '{sheetName}' がありません: {path}");

        var header = ExcelHelper.FindDayNumberRow(sheet, scanRows: 20, scanCols: 40, minRun: 5)
            ?? throw new ArgumentException($"日番号行を検出できません: {path} [{sheetName}]");

        var dayNumbers = new List<(int Day, int Col)>();
        for (int i = 0; i < header.DayCount; i++) dayNumbers.Add((i + 1, header.StartColumn + i));

        var metaRows = new List<int>();
        for (int r = header.RowIndex + 1; r <= sheet.LastRowNum; r++)
            if (ExcelHelper.Text(sheet.GetRow(r), 0).Contains("作業番号")) metaRows.Add(r);

        var result = new Dictionary<string, Block>();
        for (int i = 0; i < metaRows.Count; i++)
        {
            int metaRow = metaRows[i];
            int blockEnd = (i + 1 < metaRows.Count ? metaRows[i + 1] : sheet.LastRowNum + 1) - 1;
            var meta = sheet.GetRow(metaRow)!;

            var name = ValueAfterLabel(meta, "名前");
            if (name.Length == 0) continue;

            var block = new Block
            {
                Key = NameNormalizer.Normalize(name),
                Name = name,
                EmployeeNo = ValueAfterLabel(meta, "作業番号"),
                Department = ValueAfterLabel(meta, "部門")
            };

            // ブロック内の行を「備考行 → 打刻行 → シフト行」の順に見分ける
            var rows = new List<int>();
            for (int r = metaRow + 1; r <= blockEnd; r++)
                if (sheet.GetRow(r) != null && !IsEmptyRow(sheet.GetRow(r)!, dayNumbers)) rows.Add(r);

            var noteRows = rows.Where(r => IsNoteRow(sheet.GetRow(r)!, dayNumbers)).ToList();
            var dataRows = rows.Except(noteRows).ToList();

            // 打刻行は「出退勤が連結されたセル」があることで見分ける。
            // 見分けが付かない場合は、ブロックの最後の行を打刻行として扱う。
            int punchRow = dataRows.FirstOrDefault(r => LooksLikePunchRow(sheet.GetRow(r)!, dayNumbers), -1);
            if (punchRow < 0 && dataRows.Count >= 2) punchRow = dataRows[^1];
            if (punchRow < 0 && dataRows.Count == 1) punchRow = dataRows[0];

            int shiftRow = dataRows.FirstOrDefault(r => r != punchRow, -1);

            foreach (var (day, col) in dayNumbers)
            {
                var shift = shiftRow >= 0 ? ExcelHelper.Text(sheet.GetRow(shiftRow), col) : "";
                var punch = punchRow >= 0 ? ExcelHelper.Text(sheet.GetRow(punchRow), col) : "";
                if (shift.Length > 0 || punch.Length > 0) block.Days[day] = new DayValues(shift, punch);
            }

            foreach (var r in noteRows)
                foreach (var (day, col) in dayNumbers)
                {
                    var text = ExcelHelper.Text(sheet.GetRow(r), col);
                    if (text.Length > 0) block.Note[day] = text;
                }

            result.TryAdd(block.Key, block);
        }
        return result;
    }

    private static bool IsEmptyRow(IRow row, List<(int Day, int Col)> days)
        => days.All(d => ExcelHelper.Text(row, d.Col).Length == 0);

    /// <summary>
    /// 備考行・状態行。手作業側の「遅刻」「打刻もれ」などの注記と、
    /// 本システムの状態行(遅 / 早退 / 早出 / 打刻漏れ / 要確認)が入っている行。
    /// </summary>
    private static readonly string[] StatusWords =
        { "遅", "早", "打刻漏れ", "打刻もれ", "もれ", "漏れ", "要確認", "対象外", "時間外", "？", "?" };

    private static bool IsNoteRow(IRow row, List<(int Day, int Col)> days)
    {
        var values = days.Select(d => ExcelHelper.Text(row, d.Col)).Where(v => v.Length > 0).ToList();
        if (values.Count == 0) return false;
        return values.All(v => StatusWords.Any(v.Contains));
    }

    /// <summary>
    /// 打刻行かどうか。出退勤が連結された「06:4917:07」のセルがあれば打刻行と判断する。
    /// シフト行の時刻は「6:00」のように1セル1件しか入らないため、これで見分けられる。
    /// </summary>
    private static bool LooksLikePunchRow(IRow row, List<(int Day, int Col)> days)
        => days.Any(d => TimeText.Extract(ExcelHelper.Text(row, d.Col)).Count >= 2);

    /// <summary>「名前:」のようなラベルの右6列以内で、最初に値の入っているセルを返す。</summary>
    private static string ValueAfterLabel(IRow row, string label)
    {
        int last = row.LastCellNum;
        for (int c = 0; c < last; c++)
        {
            if (!ExcelHelper.Text(row, c).Contains(label)) continue;
            for (int v = c + 1; v <= c + 6 && v < last; v++)
            {
                var text = ExcelHelper.Text(row, v);
                if (text.Length > 0) return text;
            }
        }
        return string.Empty;
    }
}
