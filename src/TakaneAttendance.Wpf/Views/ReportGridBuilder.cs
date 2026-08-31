using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Reporting;
using TakaneAttendance.Wpf.ViewModels;

namespace TakaneAttendance.Wpf.Views;

/// <summary>
/// 出席記録レポート画面の列を組み立てる。
/// 日列の本数は対象月によって 28〜31 と変わるため、XAML では固定できずコードで生成する。
///
/// 1セルは4行(仕様書 v3.0 第8.1章)。
///   1行目 予定シフト   2行目 打刻1回目   3行目 最終打刻   4行目 判定結果
/// 異常判定の日は4行すべてを主判定の色で塗る(仕様書 15.1)。
/// </summary>
internal static class ReportGridBuilder
{
    // ---- 表示サイズ。4行(予定シフト・打刻1回目・最終打刻・判定結果)が1行に収まるよう決める ----
    // 変更するときは MainWindow.xaml の ReportGrid の RowHeight も合わせて見直すこと。

    /// <summary>日セルの文字サイズ。</summary>
    private const double DayFontSize = 12.0;
    /// <summary>4行のうち1行分の高さ。RowHeight はこの4倍 + セルの余白で決まる。</summary>
    private const double LineHeight = 15.0;
    /// <summary>1セルの行数(予定シフト / 打刻1回目 / 最終打刻 / 判定結果)。</summary>
    public const int CellLineCount = 4;
    /// <summary>DataGrid の RowHeight。4行 + セルの上下余白。</summary>
    public const double RowHeight = LineHeight * CellLineCount + 4;
    /// <summary>日列の幅。「07:54」が切れず、31日分が横スクロールなしで収まる幅にする。</summary>
    private const double DayColumnWidth = 54.0;
    /// <summary>見出しの曜日(日番号より一回り小さく出す)。</summary>
    private const double DayOfWeekFontSize = 11.0;
    /// <summary>固定列(No. / 社員番号 / 氏名(部署))の幅。日列を31本出すため狭くしている。</summary>
    private const double EmployeeColumnWidth = 118.0;

    private static readonly Brush WeekendBg  = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF3, 0xF6)));
    private static readonly Brush SundayFg   = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xC9, 0xC0)));
    private static readonly Brush SaturdayFg = Freeze(new SolidColorBrush(Color.FromRgb(0xC6, 0xE2, 0xFF)));
    private static readonly Brush InkBrush   = Freeze(new SolidColorBrush(Color.FromRgb(0x22, 0x30, 0x3F)));
    private static readonly Brush ShiftFg    = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x5A, 0xA8)));
    private static readonly Brush SubFg      = Freeze(new SolidColorBrush(Color.FromRgb(0x5B, 0x6B, 0x7C)));

    /// <summary>判定の重大度ごとの背景色(突合結果タブの行の色と揃える)。</summary>
    // 主判定の背景色。仕様書 v3.0 第15.1章で指定された Hex をそのまま使う。
    private static readonly Brush LateBg       = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xCC, 0xCC)));
    private static readonly Brush EarlyInBg    = Freeze(new SolidColorBrush(Color.FromRgb(0xFC, 0xE5, 0xCD)));
    private static readonly Brush EarlyLeaveBg = Freeze(new SolidColorBrush(Color.FromRgb(0xD9, 0xEA, 0xD3)));
    private static readonly Brush ReviewBg     = Freeze(new SolidColorBrush(Color.FromRgb(0xE4, 0xD7, 0xF5)));
    private static readonly Brush ExcludedBg   = Freeze(new SolidColorBrush(Color.FromRgb(0xE7, 0xE6, 0xE6)));

    /// <summary>画面で編集したセルの目印(左端の帯)。</summary>
    private static readonly Brush EditedMark = Freeze(new SolidColorBrush(Color.FromRgb(0xE2, 0x8C, 0x18)));

    private static readonly Brush SelectedBg = Freeze(new SolidColorBrush(Color.FromRgb(0xC7, 0xDF, 0xF7)));

    /// <summary>突合のたびに列を作り直す。</summary>
    public static void Rebuild(DataGrid grid, ReportSheet? sheet)
    {
        grid.Columns.Clear();
        grid.FrozenColumnCount = 0;
        if (sheet == null || sheet.DayCount <= 0) return;

        grid.RowHeight = RowHeight;
        grid.Columns.Add(EmployeeColumn());
        grid.FrozenColumnCount = 1;

        for (int day = 1; day <= sheet.DayCount; day++)
        {
            // 見出しの色と休場日のグレー表示は祝日マスタで決まる(仕様書 15.1・15.2)
            grid.Columns.Add(DayColumn(day, sheet.DayOfWeekText(day),
                                       sheet.HeaderToneOf(day), sheet.IsClosed(day)));
        }
    }

    /// <summary>
    /// 社員情報の列。日セルと同じ3段にして、上から 作業番号 / 氏名 / 部門 を出す。
    /// 日セルの3段(シフト・出勤・退勤)と行の高さが揃うため、横に目で追いやすい。
    /// </summary>
    private static DataGridTemplateColumn EmployeeColumn()
    {
        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 1, 6, 1)));
        style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));
        style.Setters.Add(new Setter(Control.VerticalContentAlignmentProperty, VerticalAlignment.Center));
        style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Triggers.Add(SelectedTrigger());

        return new DataGridTemplateColumn
        {
            Header = EmployeeHeader(),
            Width = new DataGridLength(EmployeeColumnWidth),
            CellStyle = style,
            CellTemplate = EmployeeTemplate(),
            CellEditingTemplate = EmployeeEditTemplate()
        };
    }

    /// <summary>
    /// 固定列の4行(仕様書 v3.0 第8.1章 No. / 社員番号 / 氏名(部署))。
    /// 日セルの4行と高さが揃うため、横に目で追いやすい。
    /// </summary>
    private static DataTemplate EmployeeTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.AppendChild(MetaLine(nameof(ReportRow.RowNumberText), SubFg, bold: false));
        panel.AppendChild(MetaLine(nameof(ReportRow.EmployeeNo), SubFg, bold: false));
        panel.AppendChild(MetaLine(nameof(ReportRow.Name), InkBrush, bold: true));
        panel.AppendChild(MetaLine(nameof(ReportRow.DepartmentText), SubFg, bold: false));
        return new DataTemplate { VisualTree = panel };
    }

    /// <summary>編集用の4行。表示と同じ並びにして、どの欄を直しているか迷わないようにする。</summary>
    private static DataTemplate EmployeeEditTemplate()
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.AppendChild(MetaLine(nameof(ReportRow.RowNumberText), SubFg, bold: false));
        panel.AppendChild(MetaEditLine(nameof(ReportRow.EmployeeNo)));
        panel.AppendChild(MetaEditLine(nameof(ReportRow.Name)));
        panel.AppendChild(MetaEditLine(nameof(ReportRow.Department)));
        return new DataTemplate { VisualTree = panel };
    }

    /// <summary>3段のうちの1行。日セルと同じ行高にして段を揃える。</summary>
    private static FrameworkElementFactory MetaLine(string path, Brush foreground, bool bold)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        text.SetValue(TextBlock.ForegroundProperty, foreground);
        text.SetValue(TextBlock.FontSizeProperty, bold ? DayFontSize : DayFontSize - 1.0);
        text.SetValue(TextBlock.FontWeightProperty, bold ? FontWeights.Bold : FontWeights.Normal);
        text.SetValue(FrameworkElement.HeightProperty, LineHeight);
        text.SetValue(TextBlock.LineHeightProperty, LineHeight);
        text.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        text.SetValue(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis);
        return text;
    }

    private static FrameworkElementFactory MetaEditLine(string path)
    {
        var box = new FrameworkElementFactory(typeof(TextBox));
        box.SetBinding(TextBox.TextProperty, new Binding(path) { Mode = BindingMode.TwoWay });
        box.SetValue(TextBlock.FontSizeProperty, DayFontSize - 1.0);
        box.SetValue(FrameworkElement.HeightProperty, LineHeight);
        box.SetValue(Control.PaddingProperty, new Thickness(1, 0, 1, 0));
        box.SetValue(Control.BorderThicknessProperty, new Thickness(1));
        return box;
    }

    /// <summary>固定列の見出し(セルの並びと同じ順で示す)。</summary>
    private static TextBlock EmployeeHeader()
    {
        var block = new TextBlock { TextAlignment = TextAlignment.Left, Foreground = Brushes.White };
        block.Inlines.Add(new Run("No. / 社員番号"));
        block.Inlines.Add(new LineBreak());
        block.Inlines.Add(new Run("氏名(部署)") { FontSize = DayOfWeekFontSize });
        return block;
    }

    /// <summary>1日分の列。</summary>
    private static DayCellColumn DayColumn(int day, string dayOfWeek, string tone, bool isClosed)
    {
        int index = day - 1;
        bool isWeekend = tone != "平";

        var style = new Style(typeof(DataGridCell));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0, 1, 0, 1)));
        // 休場日は列ごとグレー。土日祝は薄く網掛けして平日と区別する。
        style.Setters.Add(new Setter(Control.BackgroundProperty,
            isClosed ? ExcludedBg : isWeekend ? WeekendBg : Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, ToolTipBinding(index)));

        // 矛盾のあるセルを重大度で色分けする
        // 優先順位の低い判定から順に足す(後の DataTrigger が勝つため)
        // 時間外は提出する帳票に載せないため、正常と同じ白のままにして色は付けない
        style.Triggers.Add(JudgementTrigger(index, Judgement.EarlyIn, EarlyInBg));
        style.Triggers.Add(JudgementTrigger(index, Judgement.EarlyLeave, EarlyLeaveBg));
        style.Triggers.Add(JudgementTrigger(index, Judgement.Late, LateBg));
        style.Triggers.Add(JudgementTrigger(index, Judgement.Review, ReviewBg));
        style.Triggers.Add(JudgementTrigger(index, Judgement.Excluded, ExcludedBg));

        // 画面で書き換えたセルは左端に帯を出す(背景色は判定の表示に使うため)
        var edited = new DataTrigger { Binding = new Binding($"[{index}].Edited"), Value = true };
        edited.Setters.Add(new Setter(Control.BorderBrushProperty, EditedMark));
        edited.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(3, 0, 0, 0)));
        style.Triggers.Add(edited);

        // 後に足したトリガーが勝つため、選択中の見た目はここで上書きする
        style.Triggers.Add(SelectedTrigger());

        return new DayCellColumn(index)
        {
            Header = DayHeader(day, dayOfWeek, tone),
            Width = new DataGridLength(DayColumnWidth),
            CellStyle = style,
            CellTemplate = DayCellTemplate(index),
            IsReadOnly = true   // 値の変更はポップアップ(CellEditorWindow)から行う
        };
    }

    /// <summary>
    /// 1セルの中身。仕様書 v3.0 第8.1章の4行。
    ///   予定シフト / 打刻1回目 / 最終打刻 / 判定結果
    ///
    /// 予定シフトは、画面で修正した日だけ青文字にする(仕様書 15.2)。
    /// </summary>
    private static DataTemplate DayCellTemplate(int index)
    {
        var panel = new FrameworkElementFactory(typeof(StackPanel));

        panel.AppendChild(ShiftLine(index));
        panel.AppendChild(Line($"[{index}].PunchInText", InkBrush, bold: false));
        panel.AppendChild(Line($"[{index}].PunchOutText", InkBrush, bold: false));
        panel.AppendChild(Line($"[{index}].JudgementLabel", SubFg, bold: false));

        return new DataTemplate { VisualTree = panel };
    }

    /// <summary>予定シフトの行。未変更は黒、画面で修正した値は青文字(仕様書 15.2)。</summary>
    private static FrameworkElementFactory ShiftLine(int index)
    {
        var text = Line($"[{index}].ShiftText", InkBrush, bold: true);

        var style = new Style(typeof(TextBlock));
        var edited = new DataTrigger { Binding = new Binding($"[{index}].Edited"), Value = true };
        edited.Setters.Add(new Setter(TextBlock.ForegroundProperty, ShiftFg));
        style.Triggers.Add(edited);
        text.SetValue(FrameworkElement.StyleProperty, style);

        return text;
    }

    /// <summary>3段のうちの1行。空欄でも高さを保ち、どの社員も同じ位置に並ぶようにする。</summary>
    private static FrameworkElementFactory Line(string path, Brush foreground, bool bold)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(path));
        text.SetValue(TextBlock.TextAlignmentProperty, TextAlignment.Center);
        text.SetValue(TextBlock.ForegroundProperty, foreground);
        text.SetValue(TextBlock.FontSizeProperty, DayFontSize);
        text.SetValue(TextBlock.FontWeightProperty, bold ? FontWeights.Bold : FontWeights.Normal);
        text.SetValue(FrameworkElement.HeightProperty, LineHeight);
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Stretch);
        text.SetValue(TextBlock.LineHeightProperty, LineHeight);
        text.SetValue(TextBlock.LineStackingStrategyProperty, LineStackingStrategy.BlockLineHeight);
        return text;
    }

    private static Binding ToolTipBinding(int index) => new($"[{index}].ToolTipText");

    private static DataTrigger JudgementTrigger(int index, Judgement judgement, Brush background)
    {
        var trigger = new DataTrigger { Binding = new Binding($"[{index}].Judgement"), Value = judgement };
        trigger.Setters.Add(new Setter(Control.BackgroundProperty, background));
        return trigger;
    }

    /// <summary>
    /// 選択中のセルの見た目。
    /// 既定のテーマは「濃い青の背景 + 白文字」だが、この画面は背景色を自前で指定しているため
    /// 白文字だけが残って値が読めなくなる。薄い青の背景と濃い文字色を明示して防ぐ。
    /// </summary>
    private static Trigger SelectedTrigger()
    {
        var trigger = new Trigger { Property = DataGridCell.IsSelectedProperty, Value = true };
        trigger.Setters.Add(new Setter(Control.BackgroundProperty, SelectedBg));
        trigger.Setters.Add(new Setter(Control.ForegroundProperty, InkBrush));
        return trigger;
    }

    /// <summary>
    /// 「日番号 / 曜日」の2段見出し。
    /// 土は青、日曜と祝日は赤(仕様書 v3.0 第15.2章)。祝日は祝日マスタで判定する。
    /// </summary>
    private static TextBlock DayHeader(int day, string dayOfWeek, string tone)
    {
        var foreground = tone switch
        {
            "日" => SundayFg,
            "土" => SaturdayFg,
            _ => Brushes.White
        };

        var block = new TextBlock { TextAlignment = TextAlignment.Center, Foreground = foreground };
        block.Inlines.Add(new Run(day.ToString()));
        block.Inlines.Add(new LineBreak());
        block.Inlines.Add(new Run(dayOfWeek) { FontSize = DayOfWeekFontSize });
        return block;
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
