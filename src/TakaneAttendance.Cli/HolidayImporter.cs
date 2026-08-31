using NPOI.SS.UserModel;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Masters;

namespace TakaneAttendance.Cli;

/// <summary>
/// お客様の「年間カレンダー」から祝日マスタ(holiday.xml)を作る。
///
/// カレンダーは月ごとに「日 / 日付(シリアル値) / 曜日」の3列が横へ並ぶ様式で、
/// 曜日の欄に祝日は「祝」と入る。行・列の位置は年度で変わるため決め打ちせず、
/// 「日付として読めるセルの右隣が曜日の表記になっている」組み合わせを拾う。
/// </summary>
internal static class HolidayImporter
{
    public static int Run(string bookPath, string? sheetName, string outputPath, int fromYear, int toYear)
    {
        using var wb = ExcelHelper.OpenWorkbook(bookPath);

        var sheet = sheetName != null
            ? wb.GetSheet(sheetName) ?? throw new ArgumentException($"シート '{sheetName}' がありません。")
            : PickSheet(wb);

        Console.WriteLine($"ファイル : {Path.GetFileName(bookPath)}");
        Console.WriteLine($"シート   : {sheet.SheetName}");
        Console.WriteLine($"対象期間 : {fromYear}年〜{toYear}年");
        Console.WriteLine();

        var master = new HolidayMaster();
        var markers = new Dictionary<string, int>();
        int scanned = 0;

        for (int r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            for (int c = 0; c < row.LastCellNum - 1; c++)
            {
                var date = ReadDate(row.GetCell(c), fromYear, toYear);
                if (date is not { } day) continue;

                var marker = ExcelHelper.Text(row.GetCell(c + 1)).Trim();
                if (marker.Length == 0) continue;

                scanned++;
                markers[marker] = markers.GetValueOrDefault(marker) + 1;

                if (!HolidayMaster.TryParseKind(marker, out var kind)) continue;
                // 曜日から分かる区分は登録しない(祝日と休場日だけを持つ)
                if (kind is DayKind.Weekday or DayKind.Saturday or DayKind.Sunday) continue;
                master.Register(day, kind);
            }
        }

        Console.WriteLine($"日付セル : {scanned} 件");
        Console.WriteLine("曜日欄に現れた表記:");
        foreach (var (marker, count) in markers.OrderByDescending(m => m.Value))
        {
            var known = HolidayMaster.TryParseKind(marker, out var kind);
            var note = known ? HolidayMaster.Label(kind) : "★未対応(無視しました)";
            Console.WriteLine($"    {marker,-8} {count,4} 件   {note}");
        }

        // 祝日の名称(海の日 など)は年間カレンダーに入っていないため、今あるマスタから引き継ぐ。
        // 今回の取り込みに無い月/日は持ち越さない(古い登録が残らないようにする)。
        if (File.Exists(outputPath))
        {
            var previous = HolidayMaster.Load(outputPath);
            var current = master.MonthDays.ToHashSet();
            int carried = 0;

            foreach (var monthDay in previous.MonthDays)
            {
                var note = previous.NoteOf(monthDay);
                if (note.Length == 0 || !current.Contains(monthDay)) continue;
                master.Register(monthDay, master.KindOf(monthDay), note);
                carried++;
            }
            if (carried > 0) Console.WriteLine($"  祝日の名称 {carried} 件を、今のマスタから引き継ぎました。");
        }

        File.WriteAllText(outputPath, master.ToXml($"{Path.GetFileName(bookPath)} [{sheet.SheetName}]") + Environment.NewLine);

        Console.WriteLine();
        Console.WriteLine($"出力 : {outputPath}");
        Console.WriteLine($"  {master.Summary}");

        if (master.ClosedCount == 0)
            Console.WriteLine("  [注意] 休場日の表記が見つかりませんでした。" +
                              "カレンダー上で色分けなどで表現されている場合は、holiday.xml へ手で追記してください。");
        return 0;
    }

    /// <summary>日付セルが最も多いシートを選ぶ(年度の切替版が複数あるため)。</summary>
    private static ISheet PickSheet(IWorkbook wb)
    {
        ISheet? best = null;
        int bestCount = -1;

        for (int i = 0; i < wb.NumberOfSheets; i++)
        {
            var sheet = wb.GetSheetAt(i);
            if (sheet == null) continue;

            int count = 0;
            for (int r = sheet.FirstRowNum; r <= sheet.LastRowNum; r++)
            {
                var row = sheet.GetRow(r);
                if (row == null) continue;
                for (int c = 0; c < row.LastCellNum; c++)
                    if (ReadDate(row.GetCell(c), 1990, 2100) != null) count++;
            }
            if (count > bestCount) { bestCount = count; best = sheet; }
        }

        return best ?? throw new InvalidOperationException("日付を含むシートがありません。");
    }

    /// <summary>Excel のシリアル値・日付セルを日付として読む。対象年の範囲外は無視する。</summary>
    private static DateOnly? ReadDate(ICell? cell, int fromYear, int toYear)
    {
        if (cell is not { CellType: CellType.Numeric }) return null;
        try
        {
            var value = cell.NumericCellValue;
            // 日番号(1〜31)や件数の列を日付と誤解釈しないよう、シリアル値の範囲で絞る
            if (value < 30000 || value > 60000) return null;

            var date = DateOnly.FromDateTime(DateUtil.GetJavaDate(value));
            return date.Year >= fromYear && date.Year <= toYear ? date : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
