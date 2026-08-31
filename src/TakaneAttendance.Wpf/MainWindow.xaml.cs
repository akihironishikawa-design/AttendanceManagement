using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Wpf.ViewModels;
using TakaneAttendance.Wpf.Views;

namespace TakaneAttendance.Wpf;

public partial class MainWindow : Window
{
    private MainViewModel? Vm => DataContext as MainViewModel;

    public MainWindow()
    {
        InitializeComponent();

        if (Vm is not { } vm) return;

        vm.ReportSheetChanged += (_, _) => ReportGridBuilder.Rebuild(ReportGrid, vm.ReportSheet);

        // 保留ファイルを引数で渡された場合(ファイルの関連付けや exe へのドロップ)は、そのまま開く
        var draft = Environment.GetCommandLineArgs().Skip(1)
            .FirstOrDefault(a => MainViewModel.IsDraftFile(a) && File.Exists(a));
        if (draft != null) vm.OpenDraft(draft);
    }

    // ================= ドラッグ＆ドロップ =================

    /// <summary>
    /// ファイルのドロップを受け付ける。
    ///
    /// 画面のどこに落としても取り込めるようにし(中身を見て振り分ける)、
    /// 入力欄の上に落とした場合は、その欄へ確実に入るようにしている。
    /// </summary>
    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        bool hasFiles = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = hasFiles ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (hasFiles) ShowDropHint(true);
    }

    private void Window_PreviewDragLeave(object sender, DragEventArgs e) => ShowDropHint(false);

    private void Window_Drop(object sender, DragEventArgs e) => HandleDrop(e, MainViewModel.DropSlot.Auto);

    /// <summary>入力欄へ直接ドロップした場合。中身の判別によらず、その欄に入れる。</summary>
    private void PathBox_PreviewDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void PathBox_PreviewDrop(object sender, DragEventArgs e)
    {
        var slot = ((sender as FrameworkElement)?.Tag as string) switch
        {
            "Shift" => MainViewModel.DropSlot.Shift,
            "Punch" => MainViewModel.DropSlot.Punch,
            "Template" => MainViewModel.DropSlot.Template,
            _ => MainViewModel.DropSlot.Auto
        };
        HandleDrop(e, slot);
    }

    private void HandleDrop(DragEventArgs e, MainViewModel.DropSlot slot)
    {
        ShowDropHint(false);
        e.Handled = true;

        if (Vm is not { } vm) return;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        // 中身を読んで判別するため、ファイル数によっては少し待たせる
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            vm.ApplyDroppedFiles(files, slot);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>
    /// ドラッグ中の案内。画面の高さを取らないよう、下のステータスバーに出す。
    /// (Excel ファイルは画面のどこに落としても取り込めます)
    /// </summary>
    private void ShowDropHint(bool active)
    {
        if (Vm is not { } vm) return;
        if (active) vm.StatusText = "ここで離すと取り込みます(中身を見てシフト表・打刻データに振り分けます)";
    }

    /// <summary>マスタを画面で修正する。保存された場合は、画面が持っているマスタを読み直す。</summary>
    private void EditMasters_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        var editor = new MasterEditorWindow(MasterSet.DefaultDirectory, vm.UnresolvedNames) { Owner = this };
        editor.ShowDialog();

        if (editor.Saved) vm.ReloadMasters();
    }

    // ================= 氏名未解決からの登録 =================

    private void RegisterAlias_Click(object sender, RoutedEventArgs e) => RegisterSelectedAlias();

    private void UnresolvedGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        // 見出しや空白部分のダブルクリックでは開かない
        if (FindParent<DataGridRow>(e.OriginalSource as DependencyObject) == null) return;
        RegisterSelectedAlias();
    }

    /// <summary>
    /// 選んでいる未解決の氏名を、正式氏名を選ぶだけで別名マスタへ登録する。
    /// 登録しただけでは一覧は変わらないため、続けて突合をやり直す。
    /// (編集済みのセルがある場合は、突合の実行時に破棄の確認と保留の案内が出る)
    /// </summary>
    private void RegisterSelectedAlias()
    {
        if (Vm is not { } vm || vm.SelectedUnresolved is not { } unresolved) return;

        var dialog = new AliasRegisterWindow(unresolved, vm.CanonicalCandidates()) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        if (!vm.RegisterAlias(unresolved, dialog.Canonical)) return;

        if (vm.RunCommand.CanExecute(null)) vm.RunCommand.Execute(null);
    }

    /// <summary>
    /// 閉じるときに、保留していない編集を取りこぼさないよう確認する。
    /// (帳票の編集は画面のモデルにしか無いため、ここで保留しないと失われる)
    /// </summary>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (Vm is not { HasUnsavedEdits: true } vm) return;
        if (!vm.ConfirmDiscardEdits("アプリを閉じると")) e.Cancel = true;
    }

    // ================= 共通メニュー(仕様書 v3.0 第6章 UI-002) =================

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        MessageBox.Show(
            "勤怠締め支援アプリ PoC\n\n" +
            "① シフト表・打刻データを指定して「突合実行」\n" +
            "② 色の付いた日を中心に確認し、シフトを修正\n" +
            "   打刻は申請書の提出を確認したときだけ直せます\n" +
            "③ 「出席記録レポートを出力」で帳票を出力\n\n" +
            "判定ルールとしきい値は masters フォルダの設定で変更できます。",
            "ヘルプ", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ================= 申請書出力タブ =================

    /// <summary>サマリー欄の「申請書出力へ」ボタン。申請書出力の画面に移る。</summary>
    private void OpenApplicationTab_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedItem = ExportTab;
    }

    private void SelectAllForms_Click(object sender, RoutedEventArgs e) => Vm?.Forms.SelectAll(true);

    private void ClearAllForms_Click(object sender, RoutedEventArgs e) => Vm?.Forms.SelectAll(false);

    /// <summary>チェックを付けた申請書を出力する。様式は同梱のものを使う。</summary>
    private void ExportApplicationForms_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (vm.Forms.CheckedSelected.Count == 0)
        {
            MessageBox.Show("出力する人にチェックを付けてください。", "申請書出力",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        vm.ExportApplicationForms();
    }

    /// <summary>
    /// 「出席記録レポート(編集可)」の画面から、その帳票だけを出力する。
    /// テンプレートが未指定なら、この場で選んでもらう(選んだ内容は帳票出力タブにも残る)。
    /// </summary>
    private void ExportAttendanceReport_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        // 編集中のセルがあれば確定させる(その値が取りこぼされないようにする)
        ReportGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        if (vm.ReportSheet == null)
        {
            MessageBox.Show("先に突合を実行してください。", "出席記録レポート",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (vm.AttendanceReportChoice is not { } choice)
        {
            MessageBox.Show("出席記録レポートは提供範囲外の設定になっています。", "出席記録レポート",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!choice.IsReady && !AskTemplate(choice)) return;

        vm.ExportAttendanceReport();
        vm.RefreshExportPreview();
    }

    /// <summary>出勤簿を出力する。様式は同梱のものを使う。</summary>
    private void ExportAttendanceBook_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        // 編集中のセルがあれば確定させる
        ReportGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);

        if (vm.ReportSheet == null)
        {
            MessageBox.Show("先に突合を実行してください。", "出勤簿",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        vm.ExportAttendanceBook();
    }

    /// <summary>パート・アルバイト給与計算表を出力する。様式は同梱のものを使う。</summary>
    private void ExportPartTimePayroll_Click(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;

        if (vm.ReportSheet == null)
        {
            MessageBox.Show("先に突合を実行してください。", "パート・アルバイト給与計算表",
                            MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        vm.ExportPartTimePayroll();
    }

    /// <summary>テンプレート原本を選んでもらう。選ばなければ false。</summary>
    private static bool AskTemplate(ReportChoice choice)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"{choice.Name} のテンプレート",
            Filter = "Excel ブック|*.xlsx;*.xls|すべてのファイル|*.*"
        };
        if (dialog.ShowDialog() != true) return false;

        choice.TemplatePath = dialog.FileName;
        return true;
    }

    /// <summary>
    /// 一覧の絞り込み。ラジオボタンの Tag(All / NoPunch / Colored)で切り替える。
    ///
    /// IsChecked の双方向束縛にすると、選び直したときに反応しないことがあったため、
    /// 選ばれた側のイベントだけを見て画面のモデルへ渡している。
    /// </summary>
    private void ReportFilter_Checked(object sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        if (sender is not FrameworkElement { Tag: string tag }) return;

        vm.ReportFilter = tag switch
        {
            "NoPunch" => MainViewModel.ReportFilterMode.NoPunch,
            "Colored" => MainViewModel.ReportFilterMode.Colored,
            _ => MainViewModel.ReportFilterMode.All
        };
    }

    // ================= ページ送り(仕様書 v3.0 第8.2章) =================

    private void PageFirst_Click(object sender, RoutedEventArgs e) => Vm?.MoveFirst();
    private void PagePrev_Click(object sender, RoutedEventArgs e) => Vm?.MovePrevious();
    private void PageNext_Click(object sender, RoutedEventArgs e) => Vm?.MoveNext();
    private void PageLast_Click(object sender, RoutedEventArgs e) => Vm?.MoveLast();

    // ================= 出席記録レポートのセル編集 =================

    private void ReportGrid_PreviewMouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var cell = FindParent<DataGridCell>(e.OriginalSource as DependencyObject);
        if (cell?.Column is not DayCellColumn) return;

        e.Handled = true;   // DataGrid の直接編集は行わない
        EditCell(cell);
    }

    private void ReportGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.F2 or Key.Delete or Key.Back)) return;

        var cell = FindParent<DataGridCell>(Keyboard.FocusedElement as DependencyObject);
        if (cell?.Column is not DayCellColumn day || cell.DataContext is not ReportRow row) return;

        e.Handled = true;
        if (e.Key is Key.Delete or Key.Back) row[day.DayIndex].Apply("", "", "", row[day.DayIndex].PunchValue, Vm?.History);
        else EditCell(cell);
    }

    /// <summary>セルの内容をポップアップで編集する。</summary>
    private void EditCell(DataGridCell cell)
    {
        if (Vm is not { } vm) return;
        if (cell.Column is not DayCellColumn day || cell.DataContext is not ReportRow row) return;

        var target = row[day.DayIndex];
        var editor = new CellEditorWindow(target, row.Name, vm.ShiftTypeChoices) { Owner = this };
        PlaceNear(editor, cell);

        if (editor.ShowDialog() == true)
            target.Apply(editor.ShiftResult, editor.PlannedEndResult, editor.NoteResult, editor.PunchResult, vm.History);
        cell.Focus();
    }

    /// <summary>ポップアップを対象セルのすぐ下に出す(画面外にはみ出す場合は寄せる)。</summary>
    private static void PlaceNear(Window window, FrameworkElement cell)
    {
        var source = PresentationSource.FromVisual(cell);
        if (source?.CompositionTarget == null) return;

        var device = cell.PointToScreen(new Point(0, cell.ActualHeight + 2));
        var point = source.CompositionTarget.TransformFromDevice.Transform(device);

        var area = SystemParameters.WorkArea;
        window.Left = Math.Max(area.Left, Math.Min(point.X, area.Right - window.Width));
        window.Top = point.Y + window.Height > area.Bottom
            ? Math.Max(area.Top, area.Bottom - window.Height)
            : point.Y;
    }

    private static T? FindParent<T>(DependencyObject? start) where T : DependencyObject
    {
        for (var node = start; node != null; )
        {
            if (node is T match) return match;
            node = node is Visual ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
