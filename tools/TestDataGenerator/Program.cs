using System.Text;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;

// テスト用サンプル(シフト表・打刻データ)を作り直す。
//   dotnet run --project tools\TestDataGenerator
// 出力先は sample\テスト用_*.xlsx。判定ルールを一通り踏むデータを意図的に入れてある。

Console.OutputEncoding = Encoding.UTF8;

var outputDir = args.Length > 0
    ? args[0]
    : Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "sample"));
Directory.CreateDirectory(outputDir);

const int Year = 2026;
const int Month = 9;
int daysInMonth = DateTime.DaysInMonth(Year, Month);

var employees = TestEmployee.BuildAll(Year, Month, daysInMonth);

var shiftPath = Path.Combine(outputDir, "テスト用_シフト表.xlsx");
var punchPath = Path.Combine(outputDir, "テスト用_タイムレコーダーデータ.xlsx");

ShiftBookWriter.Write(shiftPath, Year, Month, daysInMonth, employees);
PunchBookWriter.Write(punchPath, Year, Month, daysInMonth, employees);

Console.WriteLine($"シフト表     : {shiftPath}");
Console.WriteLine($"打刻データ   : {punchPath}");
Console.WriteLine($"対象年月     : {Year}年{Month}月 ({daysInMonth}日)");
Console.WriteLine();
Console.WriteLine("収録した社員:");
foreach (var e in employees)
    Console.WriteLine($"  {e.ShiftName,-10} シフト表={(e.InShift ? "○" : "×")} 打刻={(e.InPunch ? "○" : "×")}  {e.Purpose}");

/// <summary>テスト用の社員1名分。シフト表と打刻データの両方の元になる。</summary>
internal sealed class TestEmployee
{
    public required string EmployeeNo { get; init; }
    public required string Department { get; init; }
    /// <summary>シフト表に書く氏名(役職表記のテストのため打刻側と変えることがある)</summary>
    public required string ShiftName { get; init; }
    /// <summary>打刻データに書く氏名(正式氏名)</summary>
    public required string PunchName { get; init; }
    public required bool InShift { get; init; }
    public required bool InPunch { get; init; }
    public required string Purpose { get; init; }

    /// <summary>添字0 = 1日。シフト表のセル値(勤務区分の文字値、または予定開始時刻)。</summary>
    public required string[] Shift { get; init; }
    /// <summary>添字0 = 1日。打刻データのセル値(出退勤の連結表記)。</summary>
    public required string[] Punch { get; init; }

    public static List<TestEmployee> BuildAll(int year, int month, int days)
    {
        var list = new List<TestEmployee>
        {
            // ---- 通常勤務の判定(正常・遅刻・早退・早出・時間外・打刻漏れ) ----
            Regular("101", "競技課", "山田 太郎", "8:00", "17:00",
                    "正常/遅刻/早退/早出30分/時間外30分/両打刻なし/打刻1件のみ",
                    (day, s) =>
                    {
                        s[1]  = ("8:00", "08:2017:00");   //  2日 遅刻候補
                        s[2]  = ("8:00", "07:5816:20");   //  3日 早退候補
                        s[3]  = ("8:00", "07:2517:02");   //  4日 早出30分以上
                        s[4]  = ("8:00", "07:5817:45");   //  5日 時間外30分以上
                        s[7]  = ("8:00", "08:3017:40");   //  8日 遅刻候補 + 時間外30分以上
                        s[8]  = ("8:00", "");             //  9日 両打刻なし
                        s[9]  = ("8:00", "08:00");        // 10日 打刻が1件のみ + 退勤打刻漏れ
                    }),

            // ---- 勤務区分の判定(公休・有給・欠勤・出張・半休・マスタ未登録) ----
            Regular("102", "競技課", "佐藤 花子", "9:00", "18:00",
                    "有給/欠勤/出張/半休/公休・有給・欠勤に打刻あり/勤務区分マスタ未登録",
                    (day, s) =>
                    {
                        s[0] = ("有",   "");              //  1日 有給
                        s[1] = ("欠",   "");              //  2日 欠勤
                        s[2] = ("出張", "");              //  3日 出張(参考)
                        s[3] = ("公",   "09:0015:00");    //  4日 公休に打刻あり
                        s[4] = ("半",   "09:0013:00");    //  5日 半休(予定終了が未登録)
                        s[7] = ("△",   "");              //  8日 勤務区分マスタ未登録
                        s[8] = ("有",   "09:0018:00");    //  9日 有給に打刻あり
                        s[9] = ("欠",   "09:0018:00");    // 10日 欠勤に打刻あり
                    }),

            // ---- 打刻漏れ ----
            Regular("201", "営業課", "鈴木 一郎", "6:00", "15:00",
                    "打刻1件のみ/両打刻なし",
                    (day, s) =>
                    {
                        s[0] = ("6:00", "05:55");         //  1日 打刻が1件のみ
                        s[1] = ("6:00", "");              //  2日 両打刻なし
                    }),

            // ---- 役職表記(別名マスタが必要 → 氏名未解決) ----
            Regular("202", "営業課", "高橋 課長", "7:30", "16:30",
                    "役職表記のため氏名未解決(masters\\name_alias.xml に <alias source=\"高橋 課長\" canonical=\"高橋 次郎\"/> を追記すると解決する)",
                    (day, s) => { }, punchName: "高橋 次郎"),

            // ---- 矛盾のない社員(色が付かない状態の確認用) ----
            Regular("204", "営業課", "渡辺 四郎", "8:30", "17:30",
                    "全日そのまま(矛盾なしの見え方の確認用)", (day, s) => { }),
        };

        // ---- 打刻データにのみいる社員(「シフト表に載っている社員のみ対象」を外すと現れる) ----
        var onlyPunch = Regular("203", "営業課", "田中 三郎", "8:00", "17:00",
                                "シフト表に無いため「シフトなし打刻」(既定の設定では対象外)", (day, s) => { });
        list.Add(new TestEmployee
        {
            EmployeeNo = onlyPunch.EmployeeNo, Department = onlyPunch.Department,
            ShiftName = onlyPunch.ShiftName, PunchName = onlyPunch.PunchName,
            InShift = false, InPunch = true, Purpose = onlyPunch.Purpose,
            Shift = onlyPunch.Shift, Punch = onlyPunch.Punch
        });

        return list;

        // 平日は所定どおりの出退勤、土日は公休。特別な日だけ overrides で差し替える。
        TestEmployee Regular(string empNo, string dept, string name, string start, string end,
                             string purpose, Action<int, (string Shift, string Punch)[]> overrides,
                             string? punchName = null)
        {
            var cells = new (string Shift, string Punch)[days];
            for (int d = 0; d < days; d++)
            {
                var date = new DateOnly(year, month, d + 1);
                if (date.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
                {
                    cells[d] = ("公", "");
                    continue;
                }
                cells[d] = (start, $"{Shift5(start, -5)}{Shift5(end, +5)}");
            }
            overrides(0, cells);

            return new TestEmployee
            {
                EmployeeNo = empNo,
                Department = dept,
                ShiftName = name,
                PunchName = punchName ?? name,
                InShift = true,
                InPunch = true,
                Purpose = purpose,
                Shift = cells.Select(c => c.Shift).ToArray(),
                Punch = cells.Select(c => c.Punch).ToArray()
            };
        }

        // "8:00" を分単位でずらして "07:55" のような2桁表記にする(打刻はタイムレコーダーの表記)
        static string Shift5(string time, int minutes)
        {
            var parts = time.Split(':');
            var t = new TimeSpan(int.Parse(parts[0]), int.Parse(parts[1]), 0) + TimeSpan.FromMinutes(minutes);
            return $"{(int)t.TotalHours:00}:{t.Minutes:00}";
        }
    }
}

/// <summary>シフト表(営業課・競技課シフト表と同じレイアウト)を書き出す。</summary>
internal static class ShiftBookWriter
{
    private const int DepartmentColumn = 1;   // B列
    private const int NameColumn = 3;         // D列
    private const int DayStartColumn = 6;     // G列
    private const int DayHeaderRow = 6;       // 7行目

    public static void Write(string path, int year, int month, int days, List<TestEmployee> employees)
    {
        using var wb = new XSSFWorkbook();
        // 「修正」を含むシートが画面で自動選択されるため、提出版 → 修正版の順に置く
        WriteSheet(wb, $"{month}月提出", year, month, days, employees, submitted: true);
        WriteSheet(wb, $"{month}月修正", year, month, days, employees, submitted: false);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        wb.Write(fs);
    }

    private static void WriteSheet(IWorkbook wb, string sheetName, int year, int month, int days,
                                   List<TestEmployee> employees, bool submitted)
    {
        var sheet = wb.CreateSheet(sheetName);
        var timeStyle = TimeStyle(wb);
        var headStyle = BoldStyle(wb);

        Text(sheet, 0, 1, $"テスト用シフト表({sheetName})", headStyle);
        Text(sheet, 1, 1, $"{year}/{month}/1～{year}/{month}/{days}");
        Text(sheet, 2, 1, submitted ? "提出版" : "修正版(こちらが最終)");

        // 日番号行と曜日行
        Text(sheet, DayHeaderRow, DepartmentColumn, "部門", headStyle);
        Text(sheet, DayHeaderRow, NameColumn, "氏名", headStyle);
        for (int d = 0; d < days; d++)
        {
            Number(sheet, DayHeaderRow, DayStartColumn + d, d + 1);
            Text(sheet, DayHeaderRow + 1, DayStartColumn + d, DayOfWeekText(new DateOnly(year, month, d + 1)));
        }
        // 日番号の並びがここで切れるように、数値ではなく文字を置く
        Text(sheet, DayHeaderRow, DayStartColumn + days, "休日数");
        Text(sheet, DayHeaderRow + 1, NameColumn, "曜日");

        int row = DayHeaderRow + 2;
        string? lastDepartment = null;
        foreach (var e in employees.Where(e => e.InShift))
        {
            if (e.Department != lastDepartment)
            {
                Text(sheet, row, DepartmentColumn, e.Department);
                lastDepartment = e.Department;
            }
            Text(sheet, row, NameColumn, e.ShiftName);

            int dayOff = 0;
            for (int d = 0; d < days; d++)
            {
                var value = e.Shift[d];

                // 提出版は「山田 太郎 の 2日」が公休のまま。修正版で 8:00 に直っている、という想定
                if (submitted && e.ShiftName == "山田 太郎" && d == 1) value = "公";

                if (value.Length == 0) continue;
                if (value == "公") dayOff++;

                if (TryTime(value, out var time))
                {
                    var cell = Cell(sheet, row, DayStartColumn + d);
                    cell.SetCellValue(time.TotalDays);
                    cell.CellStyle = timeStyle;
                }
                else
                {
                    Text(sheet, row, DayStartColumn + d, value);
                }
            }
            Number(sheet, row, DayStartColumn + days, dayOff);
            row++;
        }

        // 集計行(氏名列が空・左側が "Sub" 始まりのため、社員として読まれないことの確認用)
        Text(sheet, row + 1, DepartmentColumn, "Sub Total");

        sheet.SetColumnWidth(DepartmentColumn, 14 * 256);
        sheet.SetColumnWidth(NameColumn, 14 * 256);
        for (int d = 0; d <= days; d++) sheet.SetColumnWidth(DayStartColumn + d, 6 * 256);
    }

    private static bool TryTime(string value, out TimeSpan time)
    {
        time = default;
        var parts = value.Split(':');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m)) return false;
        time = new TimeSpan(h, m, 0);
        return true;
    }

    private static string DayOfWeekText(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Sunday => "日", DayOfWeek.Monday => "月", DayOfWeek.Tuesday => "火",
        DayOfWeek.Wednesday => "水", DayOfWeek.Thursday => "木", DayOfWeek.Friday => "金", _ => "土"
    };

    private static ICellStyle TimeStyle(IWorkbook wb)
    {
        var style = wb.CreateCellStyle();
        style.DataFormat = wb.CreateDataFormat().GetFormat("h:mm");
        return style;
    }

    private static ICellStyle BoldStyle(IWorkbook wb)
    {
        var font = wb.CreateFont();
        font.IsBold = true;
        var style = wb.CreateCellStyle();
        style.SetFont(font);
        return style;
    }

    private static ICell Cell(ISheet sheet, int row, int col)
    {
        var r = sheet.GetRow(row) ?? sheet.CreateRow(row);
        return r.GetCell(col) ?? r.CreateCell(col);
    }

    private static void Text(ISheet sheet, int row, int col, string value, ICellStyle? style = null)
    {
        var cell = Cell(sheet, row, col);
        cell.SetCellValue(value);
        if (style != null) cell.CellStyle = style;
    }

    private static void Number(ISheet sheet, int row, int col, double value)
        => Cell(sheet, row, col).SetCellValue(value);
}

/// <summary>タイムレコーダー出力(出席記録シート・2行構成)を書き出す。</summary>
internal static class PunchBookWriter
{
    public static void Write(string path, int year, int month, int days, List<TestEmployee> employees)
    {
        using var wb = new XSSFWorkbook();

        // 実物と同じく、先頭に別シートがある状態にする(「出席記録」が選ばれることの確認用)
        var other = wb.CreateSheet("シフト情報");
        other.CreateRow(0).CreateCell(0).SetCellValue("(テスト用サンプルでは未使用)");

        var sheet = wb.CreateSheet("出席記録");
        Text(sheet, 2, 0, "勤怠時間");
        Text(sheet, 2, 2, $"{year:0000}-{month:00}-01 ~ {year:0000}-{month:00}-{days:00}");

        // 日番号行(4行目)
        for (int d = 0; d < days; d++) Cell(sheet, 3, d).SetCellValue(d + 1);

        int row = 4;
        foreach (var e in employees.Where(e => e.InPunch))
        {
            // メタ行: A="作業番号:" C=番号 / I="名前:" K=氏名 / S="部門:" U=部門
            Text(sheet, row, 0, "作業番号:");
            Text(sheet, row, 2, e.EmployeeNo);
            Text(sheet, row, 8, "名前:");
            Text(sheet, row, 10, e.PunchName);
            Text(sheet, row, 18, "部門:");
            Text(sheet, row, 20, e.Department);

            // 打刻行(メタ行の次の行 = 2行構成)
            for (int d = 0; d < days; d++)
            {
                if (e.Punch[d].Length == 0) continue;
                Text(sheet, row + 1, d, e.Punch[d]);
            }
            // 打刻が1件も無い日でも行は作る(2行構成として認識させるため)
            Cell(sheet, row + 1, 0);

            row += 2;
        }

        for (int d = 0; d < days; d++) sheet.SetColumnWidth(d, 11 * 256);

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        wb.Write(fs);
    }

    private static ICell Cell(ISheet sheet, int row, int col)
    {
        var r = sheet.GetRow(row) ?? sheet.CreateRow(row);
        return r.GetCell(col) ?? r.CreateCell(col);
    }

    private static void Text(ISheet sheet, int row, int col, string value)
        => Cell(sheet, row, col).SetCellValue(value);
}
