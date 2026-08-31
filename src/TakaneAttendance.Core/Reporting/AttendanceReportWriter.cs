using NPOI.SS.UserModel;
using NPOI.SS.Util;
using NPOI.XSSF.UserModel;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Parsing;

namespace TakaneAttendance.Core.Reporting;

/// <summary>
/// 出席記録レポート(.xlsx)のレイアウト定義。
///
/// テンプレート「templates\出席記録レポート.xlsx」の実物に合わせた固定レイアウト。
///   1〜2行目 : 表題(A1:AE2 の結合セル)
///   3行目    : 期間 / 出力日
///   4行目    : 日番号 1..31
///   5行目    : 曜日
///   6行目〜  : 社員ブロック(4行 = メタ行・シフト行・打刻行・状態行)
///
/// 日列は A 列から対象月の日数分だけ使う(31日に満たない月は、その先に罫線を引かない)。
/// </summary>
internal static class ReportFormat
{
    public const int PeriodRow = 2;
    public const int DayNumberRow = 3;
    public const int DayOfWeekRow = 4;
    public const int FirstBlockRow = 5;
    public const int BlockRowCount = 4;

    public const int DayStartColumn = 0;
    public const int MaxDayColumns = 31;

    /// <summary>メタ行の項目位置(テンプレートの記載位置に合わせる)。</summary>
    public const int EmployeeNoLabelCol = 0, EmployeeNoValueCol = 2;
    public const int NameLabelCol = 8, NameValueCol = 10;
    public const int DeptLabelCol = 18, DeptValueCol = 20;

    /// <summary>3行目の期間・出力日の位置。</summary>
    public const int PeriodLabelCol = 0, PeriodValueCol = 2;
    public const int PrintedLabelCol = 9, PrintedValueCol = 11;

    /// <summary>日列の幅(文字数 × 256)。テンプレートと同じ 4.25 文字。</summary>
    public const int DayColumnWidth = (int)(4.25 * 256);

    public const short DayNumberRowHeight = 15 * 20;
    public const short DayOfWeekRowHeight = 14 * 20;
    public const short MetaRowHeight = 16 * 20;
    public const short ShiftRowHeight = 13 * 20;
    /// <summary>状態行(遅・早退 など)の高さ。</summary>
    public const short StatusRowHeight = 13 * 20;
    /// <summary>打刻行は1打刻あたりこの高さ。打刻が3件・4件ある日でも切れないようにする。</summary>
    public const short PunchLineHeight = 11 * 20;
}

/// <summary>
/// 出席記録レポート(.xlsx)の生成。
///
/// テンプレートからは「表題・期間欄・用紙設定・列幅」だけを引き継ぎ、
/// 日番号行から下は毎回すべて作り直す。罫線・塗り・フォントもこのクラスで作った書式だけを使う。
///
/// テンプレートのセル書式を複製する方式をやめた理由:
///   テンプレートはお客様の実ファイルを元にしており、条件付き書式の名残(緑・灰の塗り、
///   斜線)や、社員ごとにばらついた罫線が残っている。それを複製すると出力にもばらつきが出る。
///
/// 書き込む中身は <see cref="ReportSheet"/> がすべて持つ。画面で編集した内容はそのモデルへの
/// 書き込みとして反映されるため、画面表示と出力帳票は必ず一致する。
/// </summary>
public sealed class AttendanceReportWriter
{
    /// <summary>突合結果から組み立てて出力する(編集を挟まない場合の入口)。</summary>
    public ReportWriteResult Write(MatchingResult result, string templatePath, string outputPath)
    {
        var data = ReportSheetBuilder.Build(result, templatePath);
        var report = Write(data, templatePath, outputPath);
        report.Messages.InsertRange(0, data.Messages);   // 組み立て時の注意も呼び出し元へ返す
        return report;
    }

    /// <summary>画面で編集済みの帳票モデルを出力する。</summary>
    public ReportWriteResult Write(ReportSheet data, string templatePath, string outputPath)
    {
        var report = new ReportWriteResult();

        if (!File.Exists(templatePath))
        {
            report.Messages.Add($"[E-RP-001] テンプレートが見つかりません: {templatePath}");
            return report;
        }
        if (data.Year == 0)
        {
            report.Messages.Add("[E-RP-006] 対象年月が確定していないため出力できません。");
            return report;
        }
        if (data.Employees.Count == 0)
        {
            report.Messages.Add("[E-RP-008] 出力する社員がありません。");
            return report;
        }

        var dir = Path.GetDirectoryName(outputPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        IWorkbook wb;
        using (var fs = new FileStream(templatePath, FileMode.Open, FileAccess.Read))
            wb = WorkbookFactory.Create(fs);

        try
        {
            if (wb is not XSSFWorkbook)
            {
                report.Messages.Add("[E-RP-009] テンプレートは .xlsx 形式で指定してください。");
                return report;
            }

            var sheet = ReportLayout.FindReportSheet(wb);
            if (sheet == null)
            {
                report.Messages.Add("[E-RP-002] 出席記録シートが見つかりません。");
                return report;
            }

            int dayCount = Math.Min(data.DayCount, ReportFormat.MaxDayColumns);
            var styles = new ReportStyles(wb);

            ClearBelowHeader(sheet);
            WriteSheetLayout(sheet, dayCount);
            WritePeriod(sheet, styles, data);
            WriteDayHeader(sheet, styles, data, dayCount);

            int cursor = ReportFormat.FirstBlockRow;
            foreach (var emp in data.Employees)
            {
                WriteEmployeeBlock(sheet, styles, data, emp, cursor, dayCount, report);
                cursor += ReportFormat.BlockRowCount;
            }

            WritePrintSetup(wb, sheet, dayCount, cursor - 1);

            using var outFs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            wb.Write(outFs, leaveOpen: false);
            report.Success = true;
            report.OutputPath = outputPath;
            report.TotalEmployees = data.Employees.Count;
            report.EditedCells = data.EditedCellCount;
        }
        finally
        {
            wb.Close();
        }

        return report;
    }

    // ---- 消去 ------------------------------------------------------------

    /// <summary>
    /// 日番号行から下をすべて消す。
    /// 行を消しただけでは結合セルが残り、次に書いた値が隠れてしまうため、結合も併せて解除する。
    /// </summary>
    private static void ClearBelowHeader(ISheet sheet)
    {
        // 行を消してもメモ(コメント)は別に残るため、先に消す。
        // 消さないと、前の社員のメモが違う日のセルに残ったまま出力される。
        if (sheet is XSSFSheet xssf)
        {
            foreach (var address in xssf.GetCellComments().Keys.ToList())
            {
                if (address.Row < ReportFormat.DayNumberRow) continue;
                sheet.GetRow(address.Row)?.GetCell(address.Column)?.RemoveCellComment();
            }
        }

        for (int i = sheet.NumMergedRegions - 1; i >= 0; i--)
            if (sheet.GetMergedRegion(i).FirstRow >= ReportFormat.DayNumberRow)
                sheet.RemoveMergedRegion(i);

        for (int r = sheet.LastRowNum; r >= ReportFormat.DayNumberRow; r--)
        {
            var row = sheet.GetRow(r);
            if (row != null) sheet.RemoveRow(row);
        }
    }

    // ---- 見出し ----------------------------------------------------------

    private static void WriteSheetLayout(ISheet sheet, int dayCount)
    {
        for (int c = 0; c < ReportFormat.MaxDayColumns; c++)
            sheet.SetColumnWidth(ReportFormat.DayStartColumn + c, ReportFormat.DayColumnWidth);

        // 対象月の日数を超える列は帳票に使わないため、幅も詰めて印刷範囲から外す
        for (int c = dayCount; c < ReportFormat.MaxDayColumns; c++)
            sheet.SetColumnWidth(ReportFormat.DayStartColumn + c, 0);
    }

    /// <summary>3行目の「期間」「出力日」。ラベルはテンプレートのもの(ふりがな付き)を残す。</summary>
    private static void WritePeriod(ISheet sheet, ReportStyles styles, ReportSheet data)
    {
        var row = sheet.GetRow(ReportFormat.PeriodRow) ?? sheet.CreateRow(ReportFormat.PeriodRow);

        EnsureLabel(row, ReportFormat.PeriodLabelCol, "期間", styles);
        EnsureLabel(row, ReportFormat.PrintedLabelCol, "出力日", styles);

        int lastDay = data.DayCount;
        SetText(row, ReportFormat.PeriodValueCol,
                $"{data.Year:0000}-{data.Month:00}-01 ～ {data.Year:0000}-{data.Month:00}-{lastDay:00}", styles.PeriodValue);
        SetText(row, ReportFormat.PrintedValueCol, DateTime.Now.ToString("yyyy-MM-dd"), styles.PeriodValue);
    }

    private static void EnsureLabel(IRow row, int col, string label, ReportStyles styles)
    {
        var cell = row.GetCell(col);
        if (cell != null && !string.IsNullOrWhiteSpace(cell.ToString())) return;   // テンプレートの表記を優先
        SetText(row, col, label, styles.PeriodLabel);
    }

    /// <summary>4行目の日番号と5行目の曜日。土日は文字色で分ける。</summary>
    private static void WriteDayHeader(ISheet sheet, ReportStyles styles, ReportSheet data, int dayCount)
    {
        var numberRow = sheet.CreateRow(ReportFormat.DayNumberRow);
        var weekRow = sheet.CreateRow(ReportFormat.DayOfWeekRow);
        numberRow.Height = ReportFormat.DayNumberRowHeight;
        weekRow.Height = ReportFormat.DayOfWeekRowHeight;

        for (int d = 1; d <= dayCount; d++)
        {
            int col = ReportFormat.DayStartColumn + d - 1;
            var tone = ToneOf(data, d);

            var numberCell = numberRow.CreateCell(col);
            numberCell.SetCellValue(d);
            numberCell.CellStyle = styles.DayNumber(tone);

            var weekCell = weekRow.CreateCell(col);
            weekCell.SetCellValue(data.DayOfWeekText(d));
            weekCell.CellStyle = styles.DayOfWeek(tone);
        }
    }

    // ---- 社員ブロック ----------------------------------------------------

    private static void WriteEmployeeBlock(
        ISheet sheet, ReportStyles styles, ReportSheet data,
        ReportEmployeeBlock emp, int metaRowIndex, int dayCount, ReportWriteResult report)
    {
        WriteMetaRow(sheet, styles, emp, metaRowIndex, dayCount);

        var shiftRow = sheet.CreateRow(metaRowIndex + 1);
        var punchRow = sheet.CreateRow(metaRowIndex + 2);
        var statusRow = sheet.CreateRow(metaRowIndex + 3);
        shiftRow.Height = ReportFormat.ShiftRowHeight;
        statusRow.Height = ReportFormat.StatusRowHeight;

        int punchLines = 2;

        for (int d = 1; d <= dayCount; d++)
        {
            int index = d - 1;
            int col = ReportFormat.DayStartColumn + index;
            var tone = ToneOf(data, d);

            // その日の状態(遅・早退 など)。記入がある日は3行とも黄色で塗る
            var status = StatusOf(emp.Judgements[index]);
            bool marked = status.Length > 0;

            // ---- シフト行: 勤務区分の文字値と、通常勤務の予定開始時刻 ----
            // 予定開始時刻は「6:00」のように先頭ゼロなしで入っている(ExcelHelper が整形済み)。
            // お客様の提出帳票・手作業の出席記録とも、この表記で時刻が載る。
            var shiftText = emp.Shift[index];

            var shiftCell = shiftRow.CreateCell(col);
            shiftCell.CellStyle = styles.Shift(tone, ShiftToneOf(shiftText), marked);
            if (shiftText.Length > 0)
            {
                shiftCell.SetCellValue(shiftText);
                report.WrittenShiftCells++;
            }

            // ---- 打刻行: タイムレコーダーの原文のまま(丸めない・分解しない) ----
            // 「06:4917:07」のように出退勤が連結された表記が、そのまま提出帳票の表記になっている。
            var punchCell = punchRow.CreateCell(col);
            punchCell.CellStyle = styles.Punch(tone, marked);
            var punchText = emp.Punch[index];
            if (punchText.Length > 0)
            {
                punchCell.SetCellValue(punchText);
                // 日列は幅が狭く折り返して段が増えるため、打刻の数だけ行高を取る
                int lines = Math.Max(TimeText.Extract(punchText).Count, punchText.Split('\n').Length);
                punchLines = Math.Max(punchLines, Math.Max(lines, 1));
                report.WrittenPunchCells++;
            }

            // ---- 状態行: 遅・早退・打刻漏れ・要確認 ----
            var statusCell = statusRow.CreateCell(col);
            statusCell.CellStyle = styles.Status(tone, marked);
            if (marked)
            {
                statusCell.SetCellValue(status);
                report.WrittenStatuses++;
            }
        }

        punchRow.Height = (short)(ReportFormat.PunchLineHeight * punchLines);

        if (emp.HasAnyValue) report.WrittenEmployees++;
        else report.EmptyEmployees.Add(emp.Name);
        if (emp.AddedFromData) report.AddedEmployees.Add(emp.Name);
    }

    /// <summary>メタ行(作業番号 / 名前 / 部門)。値は結合セルにして、氏名が罫線で切れないようにする。</summary>
    private static void WriteMetaRow(ISheet sheet, ReportStyles styles, ReportEmployeeBlock emp, int rowIndex, int dayCount)
    {
        var row = sheet.CreateRow(rowIndex);
        row.Height = ReportFormat.MetaRowHeight;

        // 罫線を途切れさせないため、日列の範囲すべてにセルを作ってから結合する
        for (int c = 0; c < dayCount; c++)
        {
            var cell = row.CreateCell(ReportFormat.DayStartColumn + c);
            cell.CellStyle = styles.MetaValue;
        }

        int lastCol = ReportFormat.DayStartColumn + dayCount - 1;
        SetText(row, ReportFormat.EmployeeNoLabelCol, "作業番号:", styles.MetaLabel);
        SetEmployeeNo(row, ReportFormat.EmployeeNoValueCol, emp.EmployeeNo, styles.MetaValue);
        SetText(row, ReportFormat.NameLabelCol, "名前:", styles.MetaLabel);
        SetText(row, ReportFormat.NameValueCol, emp.Name, styles.MetaValue);
        SetText(row, ReportFormat.DeptLabelCol, "部門:", styles.MetaLabel);
        SetText(row, ReportFormat.DeptValueCol, emp.Department, styles.MetaValue);

        Merge(sheet, rowIndex, ReportFormat.EmployeeNoLabelCol, ReportFormat.EmployeeNoValueCol - 1);
        Merge(sheet, rowIndex, ReportFormat.EmployeeNoValueCol, ReportFormat.NameLabelCol - 1);
        Merge(sheet, rowIndex, ReportFormat.NameLabelCol, ReportFormat.NameValueCol - 1);
        Merge(sheet, rowIndex, ReportFormat.NameValueCol, ReportFormat.DeptLabelCol - 1);
        Merge(sheet, rowIndex, ReportFormat.DeptLabelCol, ReportFormat.DeptValueCol - 1);
        Merge(sheet, rowIndex, ReportFormat.DeptValueCol, lastCol);
    }

    private static void Merge(ISheet sheet, int rowIndex, int firstCol, int lastCol)
    {
        if (lastCol <= firstCol) return;
        sheet.AddMergedRegion(new CellRangeAddress(rowIndex, rowIndex, firstCol, lastCol));
    }

    // ---- 印刷設定 --------------------------------------------------------

    /// <summary>用紙・印刷範囲・見出しの繰り返し。テンプレートの用紙設定(A4横)はそのまま使う。</summary>
    private static void WritePrintSetup(IWorkbook wb, ISheet sheet, int dayCount, int lastRow)
    {
        sheet.CreateFreezePane(0, ReportFormat.FirstBlockRow);
        sheet.RepeatingRows = new CellRangeAddress(0, ReportFormat.DayOfWeekRow, -1, -1);

        sheet.FitToPage = true;
        sheet.PrintSetup.FitWidth = 1;
        sheet.PrintSetup.FitHeight = 0;      // 縦は必要なだけページを増やす
        sheet.PrintSetup.Landscape = true;
        sheet.IsPrintGridlines = false;

        wb.SetPrintArea(wb.GetSheetIndex(sheet),
                        ReportFormat.DayStartColumn,
                        ReportFormat.DayStartColumn + dayCount - 1,
                        0, Math.Max(lastRow, ReportFormat.DayOfWeekRow));
    }

    // ---- セル操作 --------------------------------------------------------

    private static void SetText(IRow row, int col, string text, ICellStyle style)
    {
        var cell = row.GetCell(col) ?? row.CreateCell(col);
        cell.CellStyle = style;
        if (text.Length > 0) cell.SetCellValue(text);
    }

    /// <summary>
    /// 作業番号。数字だけの番号は数値として書く。
    /// 文字列のまま書くと Excel が「数値が文字列として保存されている」の緑三角を出すため。
    /// 先頭が 0 の番号は桁を落とさないよう文字列のままにする。
    /// </summary>
    private static void SetEmployeeNo(IRow row, int col, string value, ICellStyle style)
    {
        var cell = row.GetCell(col) ?? row.CreateCell(col);
        cell.CellStyle = style;
        if (value.Length == 0) return;

        if (!value.StartsWith('0') && long.TryParse(value, out var number)) cell.SetCellValue(number);
        else cell.SetCellValue(value);
    }

    /// <summary>
    /// 状態行に書く文言。画面の判定結果行と同じ文言を使う。
    /// 正常・対象外は空欄。時間外は提出する帳票に載せないため空欄にする。
    /// </summary>
    private static string StatusOf(CellJudgement judgement)
    {
        if (!judgement.NeedsAttention) return "";
        if (judgement.Judgement == Models.Judgement.Overtime) return "";
        return judgement.Label;
    }

    private static DayTone ToneOf(ReportSheet data, int day) => data.DayOfWeekText(day) switch
    {
        "日" => DayTone.Sunday,
        "土" => DayTone.Saturday,
        _ => DayTone.Weekday
    };

    /// <summary>勤務区分の文字値から、帳票での色分けを決める。マスタ未登録の値は黒。</summary>
    private static ShiftTone ShiftToneOf(string code) => code switch
    {
        "公" or "有" or "欠" or "半" => ShiftTone.Off,
        "出張" or "本部" => ShiftTone.Outside,
        _ => ShiftTone.Plain
    };
}

/// <summary>日列の種別(土日は色を変える)。</summary>
internal enum DayTone { Weekday, Saturday, Sunday }

/// <summary>勤務区分の色分け。</summary>
internal enum ShiftTone { Plain, Off, Outside }

/// <summary>
/// 帳票で使う書式一式。
///
/// 罫線・塗り・フォントをこのクラスだけで作り、テンプレートのセル書式は複製しない。
/// 同じ見た目のセルが書式を共有するよう、生成した書式は組み合わせごとに使い回す
/// (Excel の書式数には上限があるため、社員ごとに作ってはいけない)。
/// </summary>
internal sealed class ReportStyles
{
    private static readonly byte[] WeekendFill = { 0xF2, 0xF2, 0xF2 };
    /// <summary>状態(遅・早退 など)を書いた日の塗り。その日の3行すべてに使う。</summary>
    private static readonly byte[] StatusFill = { 0xFF, 0xFF, 0x00 };
    private static readonly byte[] HeaderFill = { 0xE9, 0xEE, 0xF4 };
    private static readonly byte[] MetaFill = { 0xF7, 0xF7, 0xF7 };

    private readonly IWorkbook _wb;
    private readonly Dictionary<string, ICellStyle> _cache = new();

    private readonly IFont _headerFont;
    private readonly IFont _saturdayFont;
    private readonly IFont _sundayFont;
    private readonly IFont _weekFont;
    private readonly IFont _metaLabelFont;
    private readonly IFont _metaValueFont;
    private readonly IFont _punchFont;
    private readonly IFont _statusFont;
    private readonly IFont _periodFont;
    private readonly Dictionary<ShiftTone, IFont> _shiftFonts = new();

    public ReportStyles(IWorkbook wb)
    {
        _wb = wb;

        _headerFont   = Font("ＭＳ Ｐゴシック", 10, bold: true);
        _saturdayFont = Font("ＭＳ Ｐゴシック", 10, bold: true, color: IndexedColors.Blue.Index);
        _sundayFont   = Font("ＭＳ Ｐゴシック", 10, bold: true, color: IndexedColors.Red.Index);
        _weekFont     = Font("ＭＳ Ｐゴシック", 9);
        _metaLabelFont = Font("ＭＳ Ｐゴシック", 9);
        _metaValueFont = Font("ＭＳ Ｐゴシック", 10, bold: true);
        _punchFont    = Font("Arial", 8);
        _statusFont   = Font("ＭＳ Ｐゴシック", 9, bold: true);
        _periodFont   = Font("ＭＳ Ｐゴシック", 10);

        _shiftFonts[ShiftTone.Plain]   = Font("ＭＳ Ｐゴシック", 9, bold: true);
        _shiftFonts[ShiftTone.Off]     = Font("ＭＳ Ｐゴシック", 9, bold: true, color: IndexedColors.Red.Index);
        _shiftFonts[ShiftTone.Outside] = Font("ＭＳ Ｐゴシック", 9, bold: true, color: IndexedColors.Blue.Index);

        PeriodLabel = Get("periodLabel", s =>
        {
            s.SetFont(_periodFont);
            s.Alignment = HorizontalAlignment.Left;
            s.VerticalAlignment = VerticalAlignment.Center;
        });

        PeriodValue = Get("periodValue", s =>
        {
            s.SetFont(_periodFont);
            s.Alignment = HorizontalAlignment.Left;
            s.VerticalAlignment = VerticalAlignment.Center;
        });

        MetaLabel = Get("metaLabel", s =>
        {
            s.SetFont(_metaLabelFont);
            s.Alignment = HorizontalAlignment.Left;
            s.VerticalAlignment = VerticalAlignment.Center;
            Box(s, top: BorderStyle.Medium, bottom: BorderStyle.Thin);
            SetFill(s, MetaFill);
        });

        MetaValue = Get("metaValue", s =>
        {
            s.SetFont(_metaValueFont);
            s.Alignment = HorizontalAlignment.Left;
            s.VerticalAlignment = VerticalAlignment.Center;
            Box(s, top: BorderStyle.Medium, bottom: BorderStyle.Thin);
            SetFill(s, MetaFill);
        });
    }

    public ICellStyle PeriodLabel { get; }
    public ICellStyle PeriodValue { get; }
    public ICellStyle MetaLabel { get; }
    public ICellStyle MetaValue { get; }

    /// <summary>日番号(4行目)。</summary>
    public ICellStyle DayNumber(DayTone tone) => Get($"dayNo:{tone}", s =>
    {
        s.SetFont(tone switch
        {
            DayTone.Sunday => _sundayFont,
            DayTone.Saturday => _saturdayFont,
            _ => _headerFont
        });
        Center(s);
        Box(s, top: BorderStyle.Medium, bottom: BorderStyle.Thin);
        SetFill(s, HeaderFill);
    });

    /// <summary>曜日(5行目)。</summary>
    public ICellStyle DayOfWeek(DayTone tone) => Get($"dow:{tone}", s =>
    {
        s.SetFont(tone switch
        {
            DayTone.Sunday => _sundayFont,
            DayTone.Saturday => _saturdayFont,
            _ => _weekFont
        });
        Center(s);
        Box(s, top: BorderStyle.Thin, bottom: BorderStyle.Medium);
        SetFill(s, HeaderFill);
    });

    /// <summary>シフト行(勤務区分)。状態を書いた日は黄色で塗る。</summary>
    public ICellStyle Shift(DayTone tone, ShiftTone text, bool marked) => Get($"shift:{tone}:{text}:{marked}", s =>
    {
        s.SetFont(_shiftFonts[text]);
        Center(s);
        Box(s, top: BorderStyle.Thin, bottom: BorderStyle.Thin);
        SetDayFill(s, tone, marked);
    });

    /// <summary>打刻行(原文)。1セルに複数行入るため折り返しを有効にする。</summary>
    public ICellStyle Punch(DayTone tone, bool marked) => Get($"punch:{tone}:{marked}", s =>
    {
        s.SetFont(_punchFont);
        Center(s);
        s.WrapText = true;
        Box(s, top: BorderStyle.Thin, bottom: BorderStyle.Thin);
        SetDayFill(s, tone, marked);
    });

    /// <summary>状態行(遅・早退 など)。社員ブロックの区切りになるため、下罫線を太くする。</summary>
    public ICellStyle Status(DayTone tone, bool marked) => Get($"status:{tone}:{marked}", s =>
    {
        s.SetFont(_statusFont);
        Center(s);
        s.ShrinkToFit = true;
        Box(s, top: BorderStyle.Thin, bottom: BorderStyle.Medium);
        SetDayFill(s, tone, marked);
    });

    /// <summary>日列の塗り。状態を書いた日は黄色、土日祝は薄い網掛け、平日は塗らない。</summary>
    private static void SetDayFill(ICellStyle style, DayTone tone, bool marked)
    {
        if (marked) SetFill(style, StatusFill);
        else if (tone != DayTone.Weekday) SetFill(style, WeekendFill);
    }

    // ---- 組み立て --------------------------------------------------------

    private ICellStyle Get(string key, Action<ICellStyle> configure)
    {
        if (_cache.TryGetValue(key, out var cached)) return cached;
        var style = _wb.CreateCellStyle();
        configure(style);
        _cache[key] = style;
        return style;
    }

    private IFont Font(string name, double size, bool bold = false, short? color = null)
    {
        var font = _wb.CreateFont();
        font.FontName = name;
        font.FontHeightInPoints = (short)size;
        font.IsBold = bold;
        if (color is { } c) font.Color = c;
        return font;
    }

    private static void Center(ICellStyle s)
    {
        s.Alignment = HorizontalAlignment.Center;
        s.VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>左右は細線で固定し、上下だけ呼び出し側で変える(社員ブロックの区切りを太くするため)。</summary>
    private static void Box(ICellStyle s, BorderStyle top, BorderStyle bottom)
    {
        s.BorderLeft = BorderStyle.Thin;
        s.BorderRight = BorderStyle.Thin;
        s.BorderTop = top;
        s.BorderBottom = bottom;

        var line = IndexedColors.Grey50Percent.Index;
        s.LeftBorderColor = line;
        s.RightBorderColor = line;
        s.TopBorderColor = top == BorderStyle.Medium ? IndexedColors.Black.Index : line;
        s.BottomBorderColor = bottom == BorderStyle.Medium ? IndexedColors.Black.Index : line;
    }

    private static void SetFill(ICellStyle style, byte[] rgb)
    {
        if (style is not XSSFCellStyle xs) return;
        xs.SetFillForegroundColor(new XSSFColor(rgb, null));
        xs.FillPattern = FillPattern.SolidForeground;
    }
}

/// <summary>帳票出力の結果。</summary>
public sealed class ReportWriteResult
{
    public bool Success { get; set; }
    public string? OutputPath { get; set; }
    /// <summary>出力した社員数(全体)</summary>
    public int TotalEmployees { get; set; }
    /// <summary>1日分でも値を書き込めた社員数</summary>
    public int WrittenEmployees { get; set; }
    public int WrittenShiftCells { get; set; }
    public int WrittenPunchCells { get; set; }
    /// <summary>状態行に遅・早退などを書いた日数</summary>
    public int WrittenStatuses { get; set; }
    /// <summary>画面で編集されたセル数</summary>
    public int EditedCells { get; set; }
    /// <summary>テンプレートに無く、末尾に追加した社員</summary>
    public List<string> AddedEmployees { get; } = new();
    /// <summary>1日分も値が無く、空欄で出力した社員</summary>
    public List<string> EmptyEmployees { get; } = new();
    public List<string> Messages { get; } = new();
}
