using NPOI.SS.UserModel;
using NPOI.SS.Util;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Reporting;

/// <summary>提出する申請書の種類。様式(Materials の format_*.xls)ごとに1つ。</summary>
public enum ApplicationFormKind
{
    /// <summary>タイムカード修正届出書(打刻漏れ・打刻過多・休暇日の打刻)</summary>
    TimeCard,
    /// <summary>年次有休休暇・欠勤申請書(有給・欠勤・特別休暇)</summary>
    PaidLeave,
    /// <summary>出張届(終日出張・半日出張)</summary>
    BusinessTrip,
    /// <summary>勤怠管理簿(画面で修正した日を、社員1名につき1枚)</summary>
    AttendanceLedger
}

/// <summary>申請書の種類と、様式ファイル・申請書マスタの名称との対応。</summary>
public static class ApplicationFormKinds
{
    public const string TimeCardName = "タイムカード修正届出書";
    public const string PaidLeaveName = "年次有休休暇・欠勤申請書";
    public const string BusinessTripName = "出張届";
    public const string AttendanceLedgerName = "勤怠管理簿";

    public static readonly ApplicationFormKind[] All =
    {
        ApplicationFormKind.TimeCard, ApplicationFormKind.PaidLeave,
        ApplicationFormKind.BusinessTrip, ApplicationFormKind.AttendanceLedger
    };

    /// <summary>申請書マスタ(application_form.xml)の name と同じ表記。</summary>
    public static string NameOf(ApplicationFormKind kind) => kind switch
    {
        ApplicationFormKind.TimeCard => TimeCardName,
        ApplicationFormKind.PaidLeave => PaidLeaveName,
        ApplicationFormKind.AttendanceLedger => AttendanceLedgerName,
        _ => BusinessTripName
    };

    /// <summary>
    /// 申請書マスタの name から種類を求める。
    /// 勤怠管理簿は「その日に必要な申請書」ではなく、画面で修正した日を後から出す帳票のため、
    /// ここでは対応づけない(<see cref="AttendanceLedgerTargets"/> で対象を決める)。
    /// </summary>
    public static ApplicationFormKind? FromName(string formName) => formName switch
    {
        TimeCardName => ApplicationFormKind.TimeCard,
        PaidLeaveName => ApplicationFormKind.PaidLeave,
        BusinessTripName => ApplicationFormKind.BusinessTrip,
        _ => null
    };

    /// <summary>出力ファイル名(対象年月を差し込む)。様式の形式に合わせて拡張子を変える。</summary>
    public static string FileNameFor(ApplicationFormKind kind, int year, int month)
        => $"{NameOf(kind)}_{year}{month:00}{ExtensionOf(kind)}";

    /// <summary>様式ファイルの拡張子。勤怠管理簿だけ .xlsx。</summary>
    public static string ExtensionOf(ApplicationFormKind kind)
        => kind == ApplicationFormKind.AttendanceLedger ? ".xlsx" : ".xls";
}

/// <summary>申請書1枚分の記入内容。1件が様式1枚に対応する。</summary>
public sealed class ApplicationFormEntry
{
    public required ApplicationFormKind Kind { get; init; }
    public required string PersonName { get; init; }
    public required string Department { get; init; }
    public string EmployeeNo { get; init; } = "";

    /// <summary>対象日。年次有休休暇・欠勤申請書は続きの日をまとめるため範囲になる。</summary>
    public required DateOnly FromDate { get; init; }
    public required DateOnly ToDate { get; init; }

    /// <summary>申請書マスタの理由(打刻漏れ・有給 など)。</summary>
    public string Reason { get; init; } = "";

    /// <summary>シフト上の出社・退社時間</summary>
    public TimeSpan? PlannedStart { get; init; }
    public TimeSpan? PlannedEnd { get; init; }
    /// <summary>実質の出社・退社時間(打刻)</summary>
    public TimeSpan? ActualIn { get; init; }
    public TimeSpan? ActualOut { get; init; }
    /// <summary>遅刻・早退の時間(分)。0 なら書かない。</summary>
    public int LateMinutes { get; init; }
    public int EarlyLeaveMinutes { get; init; }
    /// <summary>終日の出張(半日出張は false)</summary>
    public bool AllDay { get; init; }

    /// <summary>画面の一覧に出す予定シフト・打刻。</summary>
    public string ShiftText { get; init; } = "";
    public string PunchText { get; init; } = "";

    /// <summary>
    /// 勤怠管理簿の対象(社員1名分)。この帳票だけは1枚に複数日を書くため、
    /// 日ごとの内容をここに持つ。他の申請書では null。
    /// </summary>
    public AttendanceLedgerPerson? Ledger { get; init; }

    /// <summary>この申請書が対象にする日数。勤怠管理簿は修正した日の数。</summary>
    public int Days => Ledger?.Days.Count ?? (ToDate.DayNumber - FromDate.DayNumber + 1);

    public string DateText
    {
        get
        {
            // 勤怠管理簿は1枚に複数日を書くため、修正した日を並べて出す
            if (Ledger is { } ledger)
            {
                var days = ledger.Days.Select(d => $"{FromDate.Month}/{d.Day}").ToList();
                return days.Count <= 8
                    ? string.Join(" , ", days)
                    : string.Join(" , ", days.Take(8)) + $" ほか {days.Count - 8} 日";
            }

            return FromDate == ToDate
                ? $"{FromDate:MM/dd}({Week(FromDate)})"
                : $"{FromDate:MM/dd}({Week(FromDate)}) 〜 {ToDate:MM/dd}({Week(ToDate)})";
        }
    }

    internal static string Week(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Sunday => "日", DayOfWeek.Monday => "月", DayOfWeek.Tuesday => "火",
        DayOfWeek.Wednesday => "水", DayOfWeek.Thursday => "木", DayOfWeek.Friday => "金", _ => "土"
    };
}

/// <summary>
/// 申請書(様式)への書き込み(勤怠締め業務フロー STEP1 ④「申請書を印刷」)。
///
/// 様式は Materials でお預かりした format_*.xls をそのまま同梱しており、画面での
/// テンプレート指定は行わない。原本を複製し、氏名・所属・対象日・勤怠情報だけを書き込む。
///
/// 1シートに様式が2枚並んでいるため、3枚以上になる場合は、2枚目の下に
/// 「2枚分の様式」をまるごと複写して(罫線・結合・行の高さごと)書き込む。
/// </summary>
public static class ApplicationFormWriter
{
    /// <summary>様式1枚分の記入位置。行番号は0始まり。</summary>
    private sealed record Slot(
        int DateRow,       // 日時(タイムカード・出張届) / 期間(年次有休)
        int ApplyRow,      // 申請日(令和 年 月 日)
        int DeptRow,       // 所　属
        int NameRow,       // 氏　名
        int ReasonRow,     // 理　由
        int InRow = -1,    // 出勤(タイムカード)
        int OutRow = -1,   // 退勤(タイムカード)
        int DaysRow = -1,  // 日数(年次有休)
        int AllDayRow = -1,// 終日(出張届)
        int TimeRow = -1); // 時間(出張届・半日)

    /// <summary>様式ごとの記入位置。1シートに2枚分あるため Slot も2つ。</summary>
    private sealed record Layout(Slot First, Slot Second, int UnitRows);

    // タイムカード修正届出書・出張届は27行間隔で2枚(申請日の行だけ2枚目が1行上にある様式のまま)
    private static readonly Layout TimeCardLayout = new(
        new Slot(DateRow: 7, ApplyRow: 19, DeptRow: 21, NameRow: 23, ReasonRow: 14, InRow: 8, OutRow: 10),
        new Slot(DateRow: 34, ApplyRow: 45, DeptRow: 48, NameRow: 50, ReasonRow: 41, InRow: 35, OutRow: 37),
        UnitRows: 54);

    private static readonly Layout BusinessTripLayout = new(
        new Slot(DateRow: 7, ApplyRow: 19, DeptRow: 21, NameRow: 23, ReasonRow: 14, AllDayRow: 8, TimeRow: 10),
        new Slot(DateRow: 34, ApplyRow: 45, DeptRow: 48, NameRow: 50, ReasonRow: 41, AllDayRow: 35, TimeRow: 37),
        UnitRows: 54);

    // 年次有休休暇・欠勤申請書は25行間隔で2枚
    private static readonly Layout PaidLeaveLayout = new(
        new Slot(DateRow: 9, ApplyRow: 17, DeptRow: 19, NameRow: 21, ReasonRow: 10, DaysRow: 8),
        new Slot(DateRow: 34, ApplyRow: 42, DeptRow: 44, NameRow: 46, ReasonRow: 35, DaysRow: 33),
        UnitRows: 50);

    private static Layout LayoutOf(ApplicationFormKind kind) => kind switch
    {
        ApplicationFormKind.TimeCard => TimeCardLayout,
        ApplicationFormKind.PaidLeave => PaidLeaveLayout,
        _ => BusinessTripLayout
    };

    /// <summary>申請書を出力する。entries の1件が様式1枚になる。</summary>
    public static ReportOutputResult Write(ApplicationFormKind kind, IReadOnlyList<ApplicationFormEntry> entries,
                                           string templatePath, string outputPath, DateOnly applyDate)
    {
        var name = ApplicationFormKinds.NameOf(kind);
        var result = new ReportOutputResult { ReportName = name, Path = outputPath };

        if (entries.Count == 0)
        {
            result.Messages.Add("対象者が選ばれていないため出力しませんでした。");
            return result;
        }
        if (!File.Exists(templatePath))
        {
            result.Messages.Add($"[{ErrorCodes.FileMissing}] 申請書の様式が見つかりません: {templatePath}");
            return result;
        }

        // 原本は更新しない。複製したファイルを開いて書き込む。
        File.Copy(templatePath, outputPath, overwrite: true);

        using (var wb = ExcelHelper.OpenWorkbook(outputPath))
        {
            var sheet = wb.GetSheetAt(0);
            var layout = LayoutOf(kind);

            // 3枚以上は「2枚分の様式」を丸ごと下に足す
            int units = (entries.Count + 1) / 2;
            for (int u = 1; u < units; u++)
            {
                CopyRows(sheet, 0, layout.UnitRows - 1, u * layout.UnitRows);
                sheet.SetRowBreak(u * layout.UnitRows - 1);
            }
            if (units > 1 && sheet.FitToPage) sheet.PrintSetup.FitHeight = (short)units;

            var form = new FormSheet(sheet);

            for (int i = 0; i < entries.Count; i++)
            {
                var slot = i % 2 == 0 ? layout.First : layout.Second;
                int offset = i / 2 * layout.UnitRows;

                switch (kind)
                {
                    case ApplicationFormKind.TimeCard: FillTimeCard(form, slot, offset, entries[i]); break;
                    case ApplicationFormKind.PaidLeave: FillPaidLeave(form, slot, offset, entries[i]); break;
                    default: FillBusinessTrip(form, slot, offset, entries[i]); break;
                }
                FillCommon(form, slot, offset, entries[i], applyDate);
                result.WrittenCells++;
            }

            result.WrittenEmployees = entries.Select(e => e.PersonName).Distinct().Count();

            // 使わなかった最後の1枚は白紙のまま残す(手書き用にそのまま印刷できる)
            if (entries.Count % 2 == 1) result.Messages.Add("最後のページの2枚目は白紙のままです。");

            sheet.ForceFormulaRecalculation = true;
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            wb.Write(fs);
        }

        result.Success = true;
        return result;
    }

    // ================= 様式ごとの記入 =================

    /// <summary>所属・氏名・申請日。3様式に共通。</summary>
    private static void FillCommon(FormSheet form, Slot slot, int offset, ApplicationFormEntry e, DateOnly applyDate)
    {
        form.Put(slot.DeptRow + offset, 5, e.Department);
        form.Put(slot.NameRow + offset, 5, e.PersonName);

        // 申請日「令和 __ 年 __ 月 __ 日」
        int applyCol = 2;   // 「令和」の次の列
        form.PutWithLabel(slot.ApplyRow + offset, applyCol, Reiwa(applyDate.Year).ToString());
        form.PutWithLabel(slot.ApplyRow + offset, applyCol + 1, applyDate.Month.ToString());
        form.PutWithLabel(slot.ApplyRow + offset, applyCol + 2, applyDate.Day.ToString());
    }

    private static void FillTimeCard(FormSheet form, Slot slot, int offset, ApplicationFormEntry e)
    {
        // 日時「令和 __ 年 __ 月 __ 日 （曜）」
        PutDate(form, slot.DateRow + offset, 3, e.FromDate);

        // 記入するのはシフト上の出社・退社時間だけ。
        // 実質の時刻と遅刻・早退時間は、ご本人に手書きしていただくため空けておく。
        PutTime(form, slot.InRow + offset, 2, e.PlannedStart);
        PutTime(form, slot.OutRow + offset, 2, e.PlannedEnd);

        form.Put(slot.ReasonRow + offset, 1, e.Reason);
    }

    private static void FillPaidLeave(FormSheet form, Slot slot, int offset, ApplicationFormEntry e)
    {
        // 「年 次 有 給 休 暇」「欠 勤」の2つの欄は1つにまとめ、どちらかを中央に書く
        form.PutMergedText(slot.DaysRow - 1 + offset, 1, 9,
                           e.Reason.Contains("有給") ? "年次有給休暇" : "欠勤");

        // 「__ 日 間」
        form.SetText(slot.DaysRow + offset, 1, $"{e.Days} 日  間");

        // 「令和 __ 年 __ 月 __ 日（曜） 〜 令和 __ 年 __ 月 __ 日（曜）」
        PutRangeDate(form, slot.DateRow + offset, 2, e.FromDate);
        PutRangeDate(form, slot.DateRow + offset, 7, e.ToDate);

        form.Put(slot.ReasonRow + offset, 1, e.Reason);
    }

    private static void FillBusinessTrip(FormSheet form, Slot slot, int offset, ApplicationFormEntry e)
    {
        PutDate(form, slot.DateRow + offset, 3, e.FromDate);

        if (e.AllDay)
        {
            // 「終　　　日」に○を付ける(この欄は結合されているため、左端のセルに書く)
            int allDayRow = slot.AllDayRow + offset;
            form.SetText(allDayRow, 0, "○ " + ExcelHelper.Text(form.Cell(allDayRow, 0)));
        }
        else
        {
            // 「時間 __ 時 __ 分 〜 __ 時 __ 分」
            PutTime(form, slot.TimeRow + offset, 2, e.ActualIn ?? e.PlannedStart);
            PutTime(form, slot.TimeRow + offset, 5, e.ActualOut ?? e.PlannedEnd);
        }

        // 「理　由 / 行  先」は勤怠データから分からないため、様式の記入欄のまま空けておく
        // (終日か時間かは上の欄で分かる)
    }

    // ================= セルの書き込み =================

    /// <summary>「令和 __ 年 __ 月 __ 日（曜）」の年月日を書く。col は「年」の列。</summary>
    private static void PutDate(FormSheet form, int row, int col, DateOnly date)
    {
        form.PutWithLabel(row, col, Reiwa(date.Year).ToString());
        form.PutWithLabel(row, col + 1, date.Month.ToString());
        form.PutWithLabel(row, col + 2, date.Day.ToString());
        form.SetFitted(row, col + 3, $"（{ApplicationFormEntry.Week(date)}）");
    }

    /// <summary>年次有休休暇の「令和 __ 年 __ 月 __ 日（曜）」。col は「年」の列。</summary>
    private static void PutRangeDate(FormSheet form, int row, int col, DateOnly date)
    {
        form.PutWithLabel(row, col, Reiwa(date.Year).ToString());
        form.PutWithLabel(row, col + 1, date.Month.ToString());
        // 日と曜日が同じ欄に入る様式のため、余分な空白を入れずに収める
        form.SetFitted(row, col + 2, $"{date.Day}日（{ApplicationFormEntry.Week(date)}）");
    }

    /// <summary>「__ 時 __ 分」。col は「時」の列。</summary>
    private static void PutTime(FormSheet form, int row, int col, TimeSpan? time)
    {
        if (time is not { } t) return;
        form.PutWithLabel(row, col, ((int)t.TotalHours).ToString());
        form.PutWithLabel(row, col + 1, t.Minutes.ToString("00"));
    }

    /// <summary>西暦から令和の年。</summary>
    private static int Reiwa(int year) => year - 2018;

    /// <summary>
    /// 書き込み先のシート。
    ///
    /// 様式は手書き用で欄が狭いため、書き込んだセルは「縮小して全体を表示する」にし、
    /// 値が枠からはみ出さないようにする。書式は元のセルから複製して足すため、
    /// 罫線や文字の配置は様式のまま変わらない。
    /// </summary>
    private sealed class FormSheet
    {
        private readonly ISheet _sheet;
        private readonly Dictionary<short, ICellStyle> _shrinkStyles = new();
        private readonly Dictionary<short, ICellStyle> _centerStyles = new();

        public FormSheet(ISheet sheet) => _sheet = sheet;

        public ICell Cell(int row, int col)
        {
            var r = _sheet.GetRow(row) ?? _sheet.CreateRow(row);
            return r.GetCell(col) ?? r.CreateCell(col);
        }

        /// <summary>空欄に値を書く(値が空なら何もしない)。</summary>
        public void Put(int row, int col, string value)
        {
            if (value.Length == 0) return;
            SetText(row, col, value);
        }

        /// <summary>
        /// 様式に印字されているラベル(「年」「時」など)の前に値を入れる。
        /// 手書き用の様式で、値を書く欄がラベルと同じセルにあるため。
        /// </summary>
        public void PutWithLabel(int row, int col, string value)
        {
            if (value.Length == 0) return;
            var label = ExcelHelper.Text(Cell(row, col));
            SetFitted(row, col, label.Length == 0 ? value : $"{value} {label}");
        }

        /// <summary>ラベルごと差し替える。右の空欄へはみ出して表示される。</summary>
        public void SetText(int row, int col, string value)
            => Cell(row, col).SetCellValue(value);

        /// <summary>
        /// 欄の幅に収まるように書く。
        /// 日付や時刻のように、右隣にも様式のラベルが入っていてはみ出せない欄で使う。
        /// </summary>
        public void SetFitted(int row, int col, string value)
        {
            var cell = Cell(row, col);
            cell.SetCellValue(value);
            Shrink(cell);
        }

        /// <summary>
        /// 欄をひとつにまとめて、中央揃えで書く。
        /// 「年次有給休暇 / 欠勤」のように、どちらかを選んで書く欄で使う。
        /// </summary>
        public void PutMergedText(int row, int firstCol, int lastCol, string value)
        {
            // まとめる前に、様式に印字されている見出しを消しておく
            for (int c = firstCol; c <= lastCol; c++) Cell(row, c).SetCellValue("");

            var remove = new List<int>();
            for (int i = 0; i < _sheet.NumMergedRegions; i++)
            {
                var m = _sheet.GetMergedRegion(i);
                if (m.FirstRow == row && m.LastRow == row &&
                    m.FirstColumn >= firstCol && m.LastColumn <= lastCol) remove.Add(i);
            }
            if (remove.Count > 0) _sheet.RemoveMergedRegions(remove);
            _sheet.AddMergedRegion(new CellRangeAddress(row, row, firstCol, lastCol));

            var cell = Cell(row, firstCol);
            cell.SetCellValue(value);
            cell.CellStyle = Centered(cell.CellStyle);
        }

        /// <summary>中央揃えにした書式(元の罫線・フォントはそのまま)。</summary>
        private ICellStyle Centered(ICellStyle source)
        {
            if (source.Alignment == HorizontalAlignment.Center) return source;

            if (!_centerStyles.TryGetValue(source.Index, out var style))
            {
                style = _sheet.Workbook.CreateCellStyle();
                style.CloneStyleFrom(source);
                style.Alignment = HorizontalAlignment.Center;
                style.Indention = 0;
                _centerStyles[source.Index] = style;
            }
            return style;
        }

        /// <summary>枠に収まらない文字を縮小して表示させる。</summary>
        private void Shrink(ICell cell)
        {
            var current = cell.CellStyle;
            if (current == null || current.ShrinkToFit) return;

            if (!_shrinkStyles.TryGetValue(current.Index, out var style))
            {
                style = _sheet.Workbook.CreateCellStyle();
                style.CloneStyleFrom(current);
                style.ShrinkToFit = true;
                _shrinkStyles[current.Index] = style;
            }
            cell.CellStyle = style;
        }
    }

    /// <summary>
    /// 様式の行をまるごと下に複写する(値・書式・結合・行の高さ)。
    /// 3枚目以降の申請書を、原本と同じ見た目で足すために使う。
    /// </summary>
    private static void CopyRows(ISheet sheet, int firstRow, int lastRow, int destRow)
    {
        int delta = destRow - firstRow;

        for (int r = firstRow; r <= lastRow; r++)
        {
            var src = sheet.GetRow(r);
            if (src == null) continue;

            var dst = sheet.GetRow(r + delta) ?? sheet.CreateRow(r + delta);
            dst.Height = src.Height;

            for (int c = src.FirstCellNum; c < src.LastCellNum && c >= 0; c++)
            {
                var sc = src.GetCell(c);
                if (sc == null) continue;

                var dc = dst.GetCell(c) ?? dst.CreateCell(c);
                dc.CellStyle = sc.CellStyle;   // 同じブック内のため書式はそのまま使える
                switch (sc.CellType)
                {
                    case CellType.String: dc.SetCellValue(sc.StringCellValue); break;
                    case CellType.Numeric: dc.SetCellValue(sc.NumericCellValue); break;
                    case CellType.Boolean: dc.SetCellValue(sc.BooleanCellValue); break;
                    case CellType.Formula: dc.CellFormula = sc.CellFormula; break;
                    default: dc.SetCellType(CellType.Blank); break;
                }
            }
        }

        // 結合は列挙中に足すと数が変わるため、一度控えてから足す
        var regions = new List<CellRangeAddress>();
        for (int i = 0; i < sheet.NumMergedRegions; i++)
        {
            var m = sheet.GetMergedRegion(i);
            if (m.FirstRow >= firstRow && m.LastRow <= lastRow) regions.Add(m);
        }
        foreach (var m in regions)
            sheet.AddMergedRegion(new CellRangeAddress(m.FirstRow + delta, m.LastRow + delta,
                                                       m.FirstColumn, m.LastColumn));
    }
}
