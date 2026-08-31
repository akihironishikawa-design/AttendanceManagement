using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Wpf.ViewModels;

namespace TakaneAttendance.Wpf.Views;

/// <summary>
/// マスタ(社員名 別名・勤務区分・勤務パターン)を画面で修正する。
/// 保存先は突合で読み込むのと同じ masters フォルダ。
/// </summary>
public partial class MasterEditorWindow : Window
{
    private readonly MasterEditorViewModel _vm;

    public MasterEditorWindow(string mastersDirectory, IEnumerable<UnresolvedName>? unresolved = null)
    {
        InitializeComponent();
        _vm = new MasterEditorViewModel(mastersDirectory, unresolved);
        DataContext = _vm;
    }

    /// <summary>一度でも保存したか。呼び出し元がマスタを読み直すかの判断に使う。</summary>
    public bool Saved => _vm.Saved;

    /// <summary>
    /// 編集中のセルを確定してから保存する。
    /// (セルの編集中に保存ボタンを押した場合、その値が取りこぼされないようにする)
    /// </summary>
    private void Save_Click(object sender, RoutedEventArgs e) => CommitEdits();

    private void Reload_Click(object sender, RoutedEventArgs e) => CommitEdits();

    private void CommitEdits()
    {
        foreach (var grid in FindGrids(this)) grid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
    }

    private static IEnumerable<DataGrid> FindGrids(DependencyObject parent)
    {
        // タブは切り替えるまで作られないため、生成済みのものだけを対象にする
        foreach (var child in LogicalTreeHelper.GetChildren(parent))
        {
            if (child is DataGrid grid) yield return grid;
            if (child is DependencyObject node)
                foreach (var found in FindGrids(node)) yield return found;
        }
    }

    /// <summary>
    /// 「管理区分」のチェックを切り替えたら、その行の編集をその場で確定する。
    /// DataGrid が編集中のままだと、チェックに合わせた一覧の絞り込み直しができない。
    /// </summary>
    private void ManagedCheckBox_Click(object sender, RoutedEventArgs e) => CommitEdits();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>保存していない編集内容があれば確認する。</summary>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!_vm.IsDirty) return;

        var answer = MessageBox.Show(
            "保存していない編集内容があります。破棄して閉じますか?",
            "マスタの編集", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (answer != MessageBoxResult.OK) e.Cancel = true;
    }
}
