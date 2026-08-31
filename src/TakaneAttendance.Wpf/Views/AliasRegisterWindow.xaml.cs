using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Wpf.ViewModels;

namespace TakaneAttendance.Wpf.Views;

/// <summary>
/// 氏名未解決の1件を、別名マスタへその場で登録するためのダイアログ。
///
/// 未解決の氏名(例「加藤 主任」)に対して、打刻データ側の正式氏名(例「加藤 十蔵」)を選ぶ。
/// 姓が一致する候補を先頭に出すため、たいていは開いてそのまま登録できる。
/// </summary>
public partial class AliasRegisterWindow : Window
{
    private readonly IReadOnlyList<CanonicalCandidate> _candidates;

    public AliasRegisterWindow(UnresolvedName unresolved, IReadOnlyList<CanonicalCandidate> candidates)
    {
        InitializeComponent();

        Unresolved = unresolved;
        _candidates = CanonicalCandidate.Rank(candidates, unresolved.SourceName);

        SourceText.Text = unresolved.SourceName;
        OriginText.Text = $"出現元: {unresolved.Origin} / {unresolved.Occurrences} 件" +
                          (string.IsNullOrWhiteSpace(unresolved.Department) ? "" : $" / 部門: {unresolved.Department}") +
                          (string.IsNullOrWhiteSpace(unresolved.EmployeeNo) ? "" : $" / 作業番号: {unresolved.EmployeeNo}");

        CandidateList.ItemsSource = _candidates;

        // 姓が一致する候補が1件だけなら、それを最初から選んでおく
        var sameSurname = _candidates.Where(c => c.MatchesSurname).ToList();
        if (sameSurname.Count == 1) CandidateList.SelectedItem = sameSurname[0];

        Loaded += (_, _) => CanonicalBox.Focus();
    }

    public UnresolvedName Unresolved { get; }

    /// <summary>登録する正式氏名。空欄の場合は「表記自体が正式氏名」として登録する。</summary>
    public string Canonical { get; private set; } = "";

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var text = FilterBox.Text.Trim();
        CandidateList.ItemsSource = text.Length == 0
            ? _candidates
            : _candidates.Where(c => c.Contains(text)).ToList();
    }

    private void CandidateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CandidateList.SelectedItem is CanonicalCandidate candidate) CanonicalBox.Text = candidate.Name;
    }

    private void CandidateList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CandidateList.SelectedItem is CanonicalCandidate) Ok_Click(sender, e);
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var canonical = CanonicalBox.Text.Trim();

        // 空欄は「表記自体が正式氏名」の登録。取り違えると突合されないため、ここで確認する。
        if (canonical.Length == 0)
        {
            var answer = MessageBox.Show(this,
                $"正式氏名が空欄です。「{Unresolved.SourceName}」自体を正式氏名として登録しますか?\n" +
                "(他の氏名と結び付けたい場合はキャンセルして候補を選んでください)",
                "別名マスタへ登録", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (answer != MessageBoxResult.OK) return;
        }

        Canonical = canonical;
        DialogResult = true;
    }
}
