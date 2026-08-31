using NPOI.SS.UserModel;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Reporting;

/// <summary>勤怠管理簿に書く1日分(画面で修正した日だけ)。</summary>
public sealed class AttendanceLedgerDay
{
    /// <summary>日(1始まり)</summary>
    public required int Day { get; init; }
    /// <summary>シフト欄。「7:00 ～16:00」または「公休→出勤」のような変更内容。</summary>
    public string ShiftText { get; init; } = "";
    /// <summary>有休･欠勤 / 出張･特別 の欄に書く1文字(有 / 欠 / 出 / 特)。</summary>
    public string LeaveMark { get; init; } = "";
    /// <summary>始業時刻(修正後の打刻)</summary>
    public string StartText { get; init; } = "";
    /// <summary>終業時刻(修正後の打刻)</summary>
    public string EndText { get; init; } = "";
    /// <summary>遅刻･早退･外出 / 振替休日変更 の欄に書く1文字(遅 / 早 / 外 / 振)。</summary>
    public string Mark { get; init; } = "";
    /// <summary>理由(画面の備考)</summary>
    public string Reason { get; init; } = "";
    /// <summary>時間外労働時間(分)。0 なら書かない。</summary>
    public int OvertimeMinutes { get; init; }
    /// <summary>土日祝か(時間外の内訳に使う)</summary>
    public bool WeekendOrHoliday { get; init; }
}

/// <summary>勤怠管理簿1枚分(社員1名)。</summary>
public sealed class AttendanceLedgerPerson
{
    public required string PersonName { get; init; }
    public string Department { get; init; } = "";
    public string EmployeeNo { get; init; } = "";
    public required IReadOnlyList<AttendanceLedgerDay> Days { get; init; }

    public int OvertimeMinutes => Days.Sum(d => d.OvertimeMinutes);
    public int WeekendOvertimeMinutes => Days.Where(d => d.WeekendOrHoliday).Sum(d => d.OvertimeMinutes);
}

/// <summary>
/// 勤怠管理簿(統合仕様書 v3.0 第16章 / 勤怠締め業務フロー ⑥)。
///
/// 業務の流れは 突合 → 出席記録レポート → 申請書 → 回収 → 画面で修正 → 勤怠管理簿 の順で、
/// この帳票には「画面で修正した日だけ」を書く。修正のあった社員1名につき1シートを作る。
///
/// 様式(Materials の【申請書】勤怠管理簿_サンプル.xlsx)をアプリに同梱しており、
/// 日付・曜日の行は対象月に合わせて書き直す。
/// </summary>
public static class AttendanceLedgerWriter
{
    /// <summary>様式の中で下敷きに使うシート。</summary>
    private const string BaseSheetName = "勤怠管理簿";

    // ---- 様式の位置(0始まり) ----
    private const int PeriodRow = 2;     // 令和○年度　　○月
    private const int PeriodCol = 0;
    private const int OwnerCol = 8;      // 部門・氏名
    private const int HeaderRow = 6;     // 1日の行
    private const int TotalRow = 37;     // 時間外労働時間合計
    private const int TotalCol = 4;

    private const int DayCol = 0;
    private const int WeekCol = 1;
    private const int ShiftCol = 2;
    private const int LeaveCol = 3;
    private const int StartCol = 4;
    private const int EndCol = 5;
    private const int OutingCol = 6;
    private const int MarkCol = 7;
    private const int ReasonCol = 8;
    private const int OvertimeCol = 9;

    /// <summary>記入する文字の大きさ(様式の記入例に合わせる)。</summary>
    private const short ValueFontPoints = 12;

    public static ReportOutputResult Write(IReadOnlyList<AttendanceLedgerPerson> people,
                                           string templatePath, string outputPath, int year, int month)
    {
        var result = new ReportOutputResult { ReportName = "勤怠管理簿", Path = outputPath };

        if (people.Count == 0)
        {
            result.Messages.Add("画面で修正した日がないため出力しませんでした。");
            return result;
        }
        if (!File.Exists(templatePath))
        {
            result.Messages.Add($"[{ErrorCodes.FileMissing}] 勤怠管理簿の様式が見つかりません: {templatePath}");
            return result;
        }

        // 原本は更新しない。複製したファイルを開いて書き込む。
        File.Copy(templatePath, outputPath, overwrite: true);

        using (var wb = ExcelHelper.OpenWorkbook(outputPath))
        {
            int baseIndex = wb.GetSheetIndex(BaseSheetName);
            if (baseIndex < 0)
            {
                result.Messages.Add($"[{ErrorCodes.StructureMissing}] 様式に「{BaseSheetName}」シートがありません。");
                return result;
            }

            // 記入例のシートは出力に残さない
            for (int i = wb.NumberOfSheets - 1; i >= 0; i--)
                if (i != baseIndex) wb.RemoveSheetAt(i);

            var usedNames = new HashSet<string>();
            var styles = new LedgerStyles(wb);
            for (int i = 0; i < people.Count; i++)
            {
                // 1人1枚。最後の1人は下敷きのシートをそのまま使う
                var sheet = i == people.Count - 1 ? wb.GetSheetAt(0) : wb.CloneSheet(0);
                wb.SetSheetName(wb.GetSheetIndex(sheet), SheetName(people[i], usedNames));

                Fill(sheet, people[i], year, month, styles);
                result.WrittenCells += people[i].Days.Count;
            }

            // 並びを人の順にそろえる(最後の1人が先頭に残るため)
            wb.SetSheetOrder(wb.GetSheetName(wb.NumberOfSheets - 1), 0);
            wb.SetActiveSheet(0);

            result.WrittenEmployees = people.Count;

            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            wb.Write(fs);
        }

        result.Success = true;
        return result;
    }

    /// <summary>シート名(31文字まで・記号は使えない)。</summary>
    private static string SheetName(AttendanceLedgerPerson person, HashSet<string> used)
    {
        var name = person.PersonName;
        foreach (var c in new[] { '[', ']', ':', '*', '?', '/', '\\' }) name = name.Replace(c, '_');
        if (name.Length > 28) name = name[..28];
        if (name.Length == 0) name = "社員";

        var unique = name;
        for (int n = 2; !used.Add(unique); n++) unique = $"{name}{n}";
        return unique;
    }

    private static void Fill(ISheet sheet, AttendanceLedgerPerson person, int year, int month, LedgerStyles styles)
    {
        // 見出し(令和○年度　　○月 / 部門 氏名)
        SetText(sheet, PeriodRow, PeriodCol, $"令和{year - 2018}年度　　　{month}月");
        SetText(sheet, PeriodRow, OwnerCol, $"{person.Department}　{person.PersonName}".Trim());

        int daysInMonth = DateTime.DaysInMonth(year, month);
        var byDay = person.Days.ToDictionary(d => d.Day);

        for (int day = 1; day <= 31; day++)
        {
            int row = HeaderRow + day - 1;

            // 対象月に無い日は、日付・曜日と記入欄の下書きを消して空欄にする
            if (day > daysInMonth)
            {
                foreach (var col in new[] { DayCol, WeekCol, LeaveCol, MarkCol, OvertimeCol })
                    SetText(sheet, row, col, "");
                continue;
            }

            var date = new DateOnly(year, month, day);
            SetNumber(sheet, row, DayCol, day);
            SetText(sheet, row, WeekCol, WeekOf(date));

            if (!byDay.TryGetValue(day, out var entry)) continue;

            SetFitted(sheet, row, ShiftCol, entry.ShiftText, styles);
            SetText(sheet, row, LeaveCol, entry.LeaveMark);      // 「有 欠 出 特」の下書きを置き換える
            // 時刻の欄は様式が20ポイントで、記入例に合わせて12ポイントに下げる
            SetSized(sheet, row, StartCol, Clock(entry.StartText), styles);
            SetSized(sheet, row, EndCol, Clock(entry.EndText), styles);
            SetText(sheet, row, MarkCol, entry.Mark);            // 「遅早外振」の下書きを置き換える
            SetFitted(sheet, row, ReasonCol, entry.Reason, styles);
            SetText(sheet, row, OvertimeCol, entry.OvertimeMinutes > 0 ? $"{entry.OvertimeMinutes} 分" : "分");
        }

        SetText(sheet, TotalRow, TotalCol,
                $"時間外労働時間合計：　{person.OvertimeMinutes}分 ( 土日祝　{person.WeekendOvertimeMinutes}分 )");
    }

    private static string WeekOf(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Sunday => "日", DayOfWeek.Monday => "月", DayOfWeek.Tuesday => "火",
        DayOfWeek.Wednesday => "水", DayOfWeek.Thursday => "木", DayOfWeek.Friday => "金", _ => "土"
    };

    /// <summary>様式に合わせて「07:24」を「7:24」にする(欄が狭いため)。</summary>
    private static string Clock(string time)
        => time.StartsWith('0') ? time[1..] : time;

    private static void SetText(ISheet sheet, int row, int col, string value)
        => Cell(sheet, row, col).SetCellValue(value);

    /// <summary>欄に収まらない文字は縮小して表示させる(シフト・理由は長くなりやすい)。</summary>
    private static void SetFitted(ISheet sheet, int row, int col, string value, LedgerStyles styles)
    {
        var cell = Cell(sheet, row, col);
        cell.SetCellValue(value);
        cell.CellStyle = styles.Shrink(cell.CellStyle);
    }

    /// <summary>文字の大きさをそろえて書く(様式の下書きが大きい欄で使う)。</summary>
    private static void SetSized(ISheet sheet, int row, int col, string value, LedgerStyles styles)
    {
        var cell = Cell(sheet, row, col);
        cell.SetCellValue(value);
        cell.CellStyle = styles.Sized(cell.CellStyle, ValueFontPoints);
    }

    private static void SetNumber(ISheet sheet, int row, int col, int value)
        => Cell(sheet, row, col).SetCellValue(value);

    private static ICell Cell(ISheet sheet, int row, int col)
    {
        var r = sheet.GetRow(row) ?? sheet.CreateRow(row);
        return r.GetCell(col) ?? r.CreateCell(col);
    }

    /// <summary>
    /// 書式の作り置き。
    /// 様式の罫線や配置はそのままに、「縮小して表示」や文字の大きさだけを変えた書式を作る。
    /// ブックあたりの書式数には上限があるため、元の書式ごとに1つだけ作って使い回す。
    /// </summary>
    private sealed class LedgerStyles
    {
        private readonly IWorkbook _workbook;
        private readonly Dictionary<short, ICellStyle> _shrink = new();
        private readonly Dictionary<(short Style, short Points), ICellStyle> _sized = new();

        public LedgerStyles(IWorkbook workbook) => _workbook = workbook;

        public ICellStyle Shrink(ICellStyle source)
        {
            if (source.ShrinkToFit) return source;

            if (!_shrink.TryGetValue(source.Index, out var style))
            {
                style = _workbook.CreateCellStyle();
                style.CloneStyleFrom(source);
                style.ShrinkToFit = true;
                _shrink[source.Index] = style;
            }
            return style;
        }

        public ICellStyle Sized(ICellStyle source, short points)
        {
            var font = _workbook.GetFontAt(source.FontIndex);
            if (font.FontHeightInPoints == points) return source;

            if (!_sized.TryGetValue((source.Index, points), out var style))
            {
                style = _workbook.CreateCellStyle();
                style.CloneStyleFrom(source);
                style.SetFont(SmallerFont(font, points));
                _sized[(source.Index, points)] = style;
            }
            return style;
        }

        /// <summary>元のフォント(書体・太さ・色)のまま、大きさだけ変えたもの。</summary>
        private IFont SmallerFont(IFont source, short points)
        {
            var found = _workbook.FindFont(source.IsBold, source.Color, points,
                                           source.FontName, source.IsItalic, source.IsStrikeout,
                                           source.TypeOffset, source.Underline);
            if (found != null) return found;

            var font = _workbook.CreateFont();
            font.FontName = source.FontName;
            font.FontHeightInPoints = points;
            font.IsBold = source.IsBold;
            font.IsItalic = source.IsItalic;
            font.Color = source.Color;
            font.Underline = source.Underline;
            return font;
        }
    }
}
