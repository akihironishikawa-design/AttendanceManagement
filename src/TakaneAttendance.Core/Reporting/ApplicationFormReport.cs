using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Reporting;

/// <summary>申請書 確認一覧の1行(社員1日1申請書)。</summary>
public sealed class ApplicationFormRow
{
    public required DateOnly WorkDate { get; init; }
    public required string EmployeeNo { get; init; }
    public required string PersonName { get; init; }
    public required string Department { get; init; }
    /// <summary>用意する申請書の名称</summary>
    public required string FormName { get; init; }
    /// <summary>一覧に出す理由(遅刻・打刻漏れ など)</summary>
    public required string Reason { get; init; }
    /// <summary>主判定の表示文言</summary>
    public required string JudgementLabel { get; init; }
    /// <summary>予定シフト(勤務区分または予定開始時刻)</summary>
    public required string ShiftText { get; init; }
    /// <summary>打刻1回目・最終打刻</summary>
    public required string PunchText { get; init; }

    public string DateText => WorkDate.ToString("MM/dd");
    public string DayOfWeekText => WorkDate.DayOfWeek switch
    {
        DayOfWeek.Sunday => "日", DayOfWeek.Monday => "月", DayOfWeek.Tuesday => "火",
        DayOfWeek.Wednesday => "水", DayOfWeek.Thursday => "木", DayOfWeek.Friday => "金", _ => "土"
    };
}

/// <summary>
/// 申請書 確認一覧(勤怠締め業務フロー STEP1 ④「申請書を印刷」)。
///
/// 突合の判定結果と申請書マスタから「どの申請書を、誰の、どの日の分で用意するか」を
/// 一覧にする。差異のある日を目視で拾ってから申請書を用意している工程を置き換える。
///
/// 判定が正常でも申請書が必要な日(有給・出張)を含める。
/// これは仕様書 v3.0 第13.3章「終日出張の日に申請書の提出済みを確認する」と同じ考え方。
/// </summary>
public static class ApplicationFormReport
{
    /// <summary>突合結果から一覧の行を作る。申請書名 → 部門 → 氏名 → 日付の順に並べる。</summary>
    public static List<ApplicationFormRow> Build(MatchingResult result, ApplicationFormMaster master)
    {
        var rows = new List<ApplicationFormRow>();

        foreach (var d in result.Details)
        {
            foreach (var form in master.Resolve(d.ResultCodes))
            {
                rows.Add(new ApplicationFormRow
                {
                    WorkDate = d.WorkDate,
                    EmployeeNo = d.EmployeeNo,
                    PersonName = d.PersonName,
                    Department = d.Department,
                    FormName = form.FormName,
                    Reason = form.Reason,
                    JudgementLabel = d.JudgementLabel,
                    ShiftText = d.ShiftText,
                    PunchText = BuildPunchText(d)
                });
            }
        }

        return rows
            .OrderBy(r => r.FormName)
            .ThenBy(r => r.Department)
            .ThenBy(r => r.PersonName)
            .ThenBy(r => r.WorkDate)
            .ToList();
    }

    private static string BuildPunchText(AttendanceDaily d)
    {
        if (d.Punch is not { PunchCount: > 0 }) return "-";
        return d.LastPunchText == "-" ? d.FirstPunchText : $"{d.FirstPunchText}〜{d.LastPunchText}";
    }

    /// <summary>
    /// 一覧を Excel に書き出す。
    ///
    /// 申請書ごとに区切って並べ、そのまま印刷の作業指示として使える形にする。
    /// 提出用の既存様式ではないため、テンプレートは使わずこちらで書式を作る。
    /// </summary>
    public static void Write(string path, IReadOnlyList<ApplicationFormRow> rows, int year, int month)
    {
        using var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet("申請書 確認一覧");
        var styles = new FormReportStyles(wb);

        int r = 0;

        // ---- 表題 ----
        var title = sheet.CreateRow(r++);
        title.HeightInPoints = 24;
        Set(title, 0, $"申請書 確認一覧　{year}年{month}月", styles.Title);
        sheet.AddMergedRegion(new CellRangeAddress(0, 0, 0, 7));

        var note = sheet.CreateRow(r++);
        Set(note, 0, $"対象 {rows.Count} 件　　突合の判定から、用意が必要な申請書を抽出したものです。", styles.Note);
        sheet.AddMergedRegion(new CellRangeAddress(1, 1, 0, 7));

        r++;   // 表題と表の間を1行あける

        // ---- 見出し ----
        var header = sheet.CreateRow(r++);
        header.HeightInPoints = 20;
        var columns = new[] { "申請書", "部門", "社員番号", "氏名", "日付", "曜", "予定シフト", "打刻", "理由" };
        for (int c = 0; c < columns.Length; c++) Set(header, c, columns[c], styles.Header);

        // ---- 明細 ----
        string? currentForm = null;
        foreach (var row in rows)
        {
            // 申請書が変わったところで小計行を挟み、印刷の単位を分かりやすくする
            if (currentForm != null && currentForm != row.FormName) r++;
            currentForm = row.FormName;

            var line = sheet.CreateRow(r++);
            Set(line, 0, row.FormName, styles.Form);
            Set(line, 1, row.Department, styles.Body);
            Set(line, 2, row.EmployeeNo, styles.Center);
            Set(line, 3, row.PersonName, styles.Body);
            Set(line, 4, row.DateText, styles.Center);
            Set(line, 5, row.DayOfWeekText, styles.Center);
            Set(line, 6, row.ShiftText, styles.Center);
            Set(line, 7, row.PunchText, styles.Center);
            Set(line, 8, row.Reason, styles.Body);
        }

        // ---- 申請書ごとの件数 ----
        if (rows.Count > 0)
        {
            r += 2;
            var summaryTitle = sheet.CreateRow(r++);
            Set(summaryTitle, 0, "申請書ごとの件数", styles.Header);
            Set(summaryTitle, 1, "件数", styles.Header);

            foreach (var g in rows.GroupBy(x => x.FormName).OrderBy(g => g.Key))
            {
                var line = sheet.CreateRow(r++);
                Set(line, 0, g.Key, styles.Body);
                Set(line, 1, g.Count().ToString(), styles.Center);
            }
        }

        int[] widths = { 30, 16, 10, 18, 10, 6, 14, 18, 30 };
        for (int c = 0; c < widths.Length; c++) sheet.SetColumnWidth(c, widths[c] * 256);

        // 見出しまでを固定し、印刷時も各ページに繰り返す
        sheet.CreateFreezePane(0, 4);
        sheet.RepeatingRows = new CellRangeAddress(3, 3, 0, 8);
        sheet.FitToPage = true;
        sheet.PrintSetup.Landscape = true;
        sheet.PrintSetup.FitWidth = 1;
        sheet.PrintSetup.FitHeight = 0;

        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        wb.Write(fs);
    }

    private static void Set(IRow row, int col, string value, ICellStyle style)
    {
        var cell = row.CreateCell(col);
        cell.SetCellValue(value);
        cell.CellStyle = style;
    }
}

/// <summary>申請書 確認一覧の書式。組み合わせごとに使い回す(Excel の書式数に上限があるため)。</summary>
internal sealed class FormReportStyles
{
    public ICellStyle Title { get; }
    public ICellStyle Note { get; }
    public ICellStyle Header { get; }
    public ICellStyle Body { get; }
    public ICellStyle Center { get; }
    public ICellStyle Form { get; }

    public FormReportStyles(IWorkbook wb)
    {
        Title = Create(wb, Font(wb, 14, bold: true), HorizontalAlignment.Left, border: false);
        Note = Create(wb, Font(wb, 9, bold: false, grey: true), HorizontalAlignment.Left, border: false);

        Header = Create(wb, Font(wb, 10, bold: true), HorizontalAlignment.Center, border: true);
        Header.FillForegroundColor = NPOI.HSSF.Util.HSSFColor.Grey25Percent.Index;
        Header.FillPattern = FillPattern.SolidForeground;

        Body = Create(wb, Font(wb, 10, bold: false), HorizontalAlignment.Left, border: true);
        Center = Create(wb, Font(wb, 10, bold: false), HorizontalAlignment.Center, border: true);
        Form = Create(wb, Font(wb, 10, bold: true), HorizontalAlignment.Left, border: true);
    }

    private static IFont Font(IWorkbook wb, short size, bool bold, bool grey = false)
    {
        var font = wb.CreateFont();
        font.FontName = "ＭＳ Ｐゴシック";
        font.FontHeightInPoints = size;
        font.IsBold = bold;
        if (grey) font.Color = NPOI.HSSF.Util.HSSFColor.Grey50Percent.Index;
        return font;
    }

    private static ICellStyle Create(IWorkbook wb, IFont font, HorizontalAlignment align, bool border)
    {
        var style = wb.CreateCellStyle();
        style.SetFont(font);
        style.Alignment = align;
        style.VerticalAlignment = VerticalAlignment.Center;
        if (border)
        {
            style.BorderTop = BorderStyle.Thin;
            style.BorderBottom = BorderStyle.Thin;
            style.BorderLeft = BorderStyle.Thin;
            style.BorderRight = BorderStyle.Thin;
        }
        return style;
    }
}
