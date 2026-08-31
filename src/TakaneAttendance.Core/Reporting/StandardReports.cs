using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Reporting;

/// <summary>
/// PoC 標準出力の帳票(統合仕様書 v3.0 第16章)。
///
///   日別集計表(部門別) … 日ごとの出勤・休暇・異常の件数
///   修正履歴一覧       … 画面での修正の前後と判定の変化
///   打刻詳細一覧       … 全打刻の原文・採用時刻・件数
///
/// お客様の既存様式がある帳票と違い、こちらで様式を作るものなので、
/// テンプレートは使わず書式もこのクラスで組み立てる。
/// </summary>
public static class StandardReports
{
    // ================= 日別集計表(部門別) =================

    /// <summary>日別集計表(部門別)。締めの進み具合を部門ごとに俯瞰するための表。</summary>
    public static ReportOutputResult WriteDailySummary(MatchingResult result, string path)
    {
        var output = new ReportOutputResult { ReportName = "日別集計表(部門別)", Path = path };

        using var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet("日別集計表");
        var styles = new SimpleStyles(wb);
        var holidays = result.Masters?.Holidays ?? new HolidayMaster();

        int r = 0;
        Title(sheet, ref r, styles, $"日別集計表(部門別)　{result.TargetYear}年{result.TargetMonth}月", 9);

        var header = sheet.CreateRow(r++);
        var labels = new[] { "部門", "日付", "曜", "出勤", "公休", "有給", "出張", "その他", "要確認" };
        for (int c = 0; c < labels.Length; c++) Set(header, c, labels[c], styles.Header);

        var groups = result.Details
            .GroupBy(d => (d.Department, d.WorkDate))
            .OrderBy(g => g.Key.Department)
            .ThenBy(g => g.Key.WorkDate);

        foreach (var g in groups)
        {
            var row = sheet.CreateRow(r++);
            var date = g.Key.WorkDate;

            Set(row, 0, g.Key.Department, styles.Body);
            Set(row, 1, date.ToString("MM/dd"), styles.Center);
            Set(row, 2, DayOfWeekText(date), ToneStyle(styles, holidays, date));

            // 出勤 = 打刻が2件以上あった日(実際に働いた日)
            Set(row, 3, Count(g, d => d.Punch is { PunchCount: >= 2 }), styles.Center);
            Set(row, 4, Count(g, d => d.ResultCodes.Contains(ResultCode.DayOff)), styles.Center);
            Set(row, 5, Count(g, d => d.ResultCodes.Contains(ResultCode.PaidLeave)
                                   || d.ResultCodes.Contains(ResultCode.PaidLeavePunch)), styles.Center);
            Set(row, 6, Count(g, d => d.ResultCodes.Contains(ResultCode.BusinessTripFull)
                                   || d.ResultCodes.Contains(ResultCode.BusinessTripHalf)), styles.Center);
            Set(row, 7, Count(g, d => d.ResultCodes.Contains(ResultCode.Other)
                                   || d.ResultCodes.Contains(ResultCode.OtherPunch)), styles.Center);
            Set(row, 8, Count(g, d => d.Judgement == Judgement.Review), styles.Center);

            output.WrittenCells += 9;
        }

        int[] widths = { 20, 10, 6, 8, 8, 8, 8, 8, 10 };
        Finish(sheet, widths, headerRow: 2);
        Save(wb, path);

        output.WrittenEmployees = result.PersonCount;
        output.Success = true;
        return output;
    }

    // ================= 修正履歴一覧 =================

    /// <summary>修正履歴一覧(仕様書 第18.1章)。締めの説明可能性のための記録。</summary>
    public static ReportOutputResult WriteEditHistory(ReportSheet sheet, string path)
    {
        var output = new ReportOutputResult { ReportName = "修正履歴一覧", Path = path };

        using var wb = new XSSFWorkbook();
        var s = wb.CreateSheet("修正履歴");
        var styles = new SimpleStyles(wb);

        int r = 0;
        Title(s, ref r, styles,
              $"修正履歴一覧　{sheet.Year}年{sheet.Month}月　(修正 {sheet.History.Count} 件 / " +
              $"判定が変わったもの {sheet.History.JudgementChangedCount} 件)", 12);

        var header = s.CreateRow(r++);
        var labels = new[] { "修正ID", "修正日時", "修正者", "社員番号", "氏名", "日付",
                             "項目", "修正前", "修正後", "修正前の判定", "修正後の判定", "備考" };
        for (int c = 0; c < labels.Length; c++) Set(header, c, labels[c], styles.Header);

        foreach (var e in sheet.History.Entries)
        {
            var row = s.CreateRow(r++);
            // 判定が変わった修正は、後から追えるよう色を変える
            var style = e.JudgementChanged ? styles.Marked : styles.Body;

            Set(row, 0, e.Id.ToString(), styles.Center);
            Set(row, 1, e.EditedAt.ToString("yyyy/MM/dd HH:mm:ss"), styles.Center);
            Set(row, 2, e.EditedBy, styles.Body);
            Set(row, 3, e.EmployeeNo, styles.Center);
            Set(row, 4, e.PersonName, styles.Body);
            Set(row, 5, e.WorkDate.ToString("MM/dd"), styles.Center);
            Set(row, 6, e.Field, styles.Body);
            Set(row, 7, Or(e.Before), styles.Center);
            Set(row, 8, Or(e.After), styles.Center);
            Set(row, 9, e.JudgementBefore, style);
            Set(row, 10, e.JudgementAfter, style);
            Set(row, 11, e.Note, styles.Body);
            output.WrittenCells += 12;
        }

        if (sheet.History.Count == 0)
            Set(s.CreateRow(r++), 0, "画面での修正はありません。", styles.Body);

        int[] widths = { 8, 20, 14, 10, 16, 10, 16, 16, 16, 12, 12, 30 };
        Finish(s, widths, headerRow: 2);
        Save(wb, path);

        output.WrittenEmployees = sheet.History.Entries.Select(e => e.PersonName).Distinct().Count();
        output.Success = true;
        return output;
    }

    // ================= 打刻詳細一覧 =================

    /// <summary>
    /// 打刻詳細一覧(仕様書 第12章)。
    /// 3件以上ある日の中間打刻など、画面では省いている情報をすべて残す。
    /// </summary>
    public static ReportOutputResult WritePunchDetail(MatchingResult result, string path)
    {
        var output = new ReportOutputResult { ReportName = "打刻詳細一覧", Path = path };

        using var wb = new XSSFWorkbook();
        var sheet = wb.CreateSheet("打刻詳細");
        var styles = new SimpleStyles(wb);

        int r = 0;
        Title(sheet, ref r, styles, $"打刻詳細一覧　{result.TargetYear}年{result.TargetMonth}月", 10);

        var header = sheet.CreateRow(r++);
        var labels = new[] { "部門", "社員番号", "氏名", "日付", "曜", "件数",
                             "打刻1回目", "最終打刻", "全打刻", "原文" };
        for (int c = 0; c < labels.Length; c++) Set(header, c, labels[c], styles.Header);

        var rows = result.Details
            .Where(d => d.Punch is { PunchCount: > 0 })
            .OrderBy(d => d.Department).ThenBy(d => d.PersonName).ThenBy(d => d.WorkDate);

        foreach (var d in rows)
        {
            var row = sheet.CreateRow(r++);
            // 3件以上は要確認の対象。目で追えるよう色を変える。
            var style = d.Punch!.PunchCount >= 3 ? styles.Marked : styles.Body;

            Set(row, 0, d.Department, styles.Body);
            Set(row, 1, d.EmployeeNo, styles.Center);
            Set(row, 2, d.PersonName, styles.Body);
            Set(row, 3, d.WorkDate.ToString("MM/dd"), styles.Center);
            Set(row, 4, d.DayOfWeekText, styles.Center);
            Set(row, 5, d.Punch.PunchCount.ToString(), style);
            Set(row, 6, d.FirstPunchText, styles.Center);
            Set(row, 7, d.LastPunchText, styles.Center);
            Set(row, 8, d.AllPunchesText, styles.Body);
            Set(row, 9, d.PunchRawText, styles.Body);
            output.WrittenCells += 10;
        }

        int[] widths = { 18, 10, 16, 10, 6, 8, 12, 12, 34, 26 };
        Finish(sheet, widths, headerRow: 2);
        Save(wb, path);

        output.WrittenEmployees = result.PersonCount;
        output.Success = true;
        return output;
    }

    // ================= 共通 =================

    private static string Count<T>(IEnumerable<T> items, Func<T, bool> match)
    {
        int n = items.Count(match);
        return n == 0 ? "" : n.ToString();
    }

    private static string Or(string value) => value.Length > 0 ? value : "(空欄)";

    private static string DayOfWeekText(DateOnly date) => date.DayOfWeek switch
    {
        DayOfWeek.Sunday => "日", DayOfWeek.Monday => "月", DayOfWeek.Tuesday => "火",
        DayOfWeek.Wednesday => "水", DayOfWeek.Thursday => "木", DayOfWeek.Friday => "金", _ => "土"
    };

    /// <summary>土は青・日祝は赤(仕様書 第15.2章)。</summary>
    private static ICellStyle ToneStyle(SimpleStyles styles, HolidayMaster holidays, DateOnly date)
        => holidays.HeaderToneOf(date) switch
        {
            "土" => styles.Saturday,
            "日" => styles.Sunday,
            _ => styles.Center
        };

    private static void Title(ISheet sheet, ref int r, SimpleStyles styles, string text, int span)
    {
        var row = sheet.CreateRow(r++);
        row.HeightInPoints = 22;
        Set(row, 0, text, styles.Title);
        sheet.AddMergedRegion(new CellRangeAddress(0, 0, 0, Math.Max(1, span - 1)));
        r++;   // 表題と表の間を1行あける
    }

    private static void Finish(ISheet sheet, int[] widths, int headerRow)
    {
        for (int c = 0; c < widths.Length; c++) sheet.SetColumnWidth(c, widths[c] * 256);
        sheet.CreateFreezePane(0, headerRow + 1);
        sheet.RepeatingRows = new CellRangeAddress(headerRow, headerRow, 0, widths.Length - 1);
        sheet.FitToPage = true;
        sheet.PrintSetup.Landscape = true;
        sheet.PrintSetup.FitWidth = 1;
        sheet.PrintSetup.FitHeight = 0;
    }

    private static void Set(IRow row, int col, string value, ICellStyle style)
    {
        var cell = row.CreateCell(col);
        cell.SetCellValue(value);
        cell.CellStyle = style;
    }

    private static void Save(IWorkbook wb, string path)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
        wb.Write(fs);
    }
}

/// <summary>PoC 標準出力で使う書式。組み合わせごとに1つだけ作って使い回す。</summary>
internal sealed class SimpleStyles
{
    public ICellStyle Title { get; }
    public ICellStyle Header { get; }
    public ICellStyle Body { get; }
    public ICellStyle Center { get; }
    public ICellStyle Saturday { get; }
    public ICellStyle Sunday { get; }
    /// <summary>目で追いたい行(判定が変わった修正・3件以上の打刻)。</summary>
    public ICellStyle Marked { get; }

    public SimpleStyles(IWorkbook wb)
    {
        Title = Make(wb, Font(wb, 14, true), HorizontalAlignment.Left, border: false);
        Header = Make(wb, Font(wb, 10, true), HorizontalAlignment.Center, border: true);
        Header.FillForegroundColor = NPOI.HSSF.Util.HSSFColor.Grey25Percent.Index;
        Header.FillPattern = FillPattern.SolidForeground;

        Body = Make(wb, Font(wb, 10, false), HorizontalAlignment.Left, border: true);
        Center = Make(wb, Font(wb, 10, false), HorizontalAlignment.Center, border: true);

        Saturday = Make(wb, Font(wb, 10, false, NPOI.HSSF.Util.HSSFColor.Blue.Index), HorizontalAlignment.Center, true);
        Sunday = Make(wb, Font(wb, 10, false, NPOI.HSSF.Util.HSSFColor.Red.Index), HorizontalAlignment.Center, true);

        Marked = Make(wb, Font(wb, 10, true), HorizontalAlignment.Center, border: true);
        Marked.FillForegroundColor = NPOI.HSSF.Util.HSSFColor.LightOrange.Index;
        Marked.FillPattern = FillPattern.SolidForeground;
    }

    private static IFont Font(IWorkbook wb, short size, bool bold, short? color = null)
    {
        var font = wb.CreateFont();
        font.FontName = "ＭＳ Ｐゴシック";
        font.FontHeightInPoints = size;
        font.IsBold = bold;
        if (color is { } c) font.Color = c;
        return font;
    }

    private static ICellStyle Make(IWorkbook wb, IFont font, HorizontalAlignment align, bool border)
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
