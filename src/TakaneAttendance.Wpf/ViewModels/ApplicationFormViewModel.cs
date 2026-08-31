using System.Collections.ObjectModel;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Reporting;

namespace TakaneAttendance.Wpf.ViewModels;

/// <summary>申請書1枚分の対象。一覧の1行で、チェックを付けたものが出力される。</summary>
public sealed class ApplicationTargetRow : ObservableObject
{
    public required ApplicationFormEntry Entry { get; init; }

    private bool _selected = true;
    /// <summary>出力するかどうか。既定は全員に付けておき、外してもらう。</summary>
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    public string PersonName => Entry.PersonName;
    public string Department => Entry.Department;
    public string EmployeeNo => Entry.EmployeeNo;
    public string DateText => Entry.DateText;
    public string DaysText => Entry.Days > 1 ? $"{Entry.Days} 日" : "";
    /// <summary>勤怠管理簿だけ、1枚に複数日を書く。</summary>
    public bool IsLedger => Entry.Ledger != null;
    public string Reason => Entry.Reason;
    public string ShiftText => Entry.ShiftText;
    public string PunchText => Entry.PunchText;
}

/// <summary>申請書の種類の切り替え(件数付き)。</summary>
public sealed class ApplicationFormChoice : ObservableObject
{
    public required ApplicationFormKind Kind { get; init; }
    public string Name => ApplicationFormKinds.NameOf(Kind);

    private int _count;
    public int Count { get => _count; set { if (Set(ref _count, value)) OnPropertyChanged(nameof(Label)); } }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set { if (Set(ref _isSelected, value) && value) SelectedChanged?.Invoke(this, EventArgs.Empty); }
    }

    public string Label => $"{Name}({Count} 件)";

    /// <summary>この種類が選ばれたことを画面のモデルへ知らせる。</summary>
    public event EventHandler? SelectedChanged;
}

/// <summary>
/// 申請書出力タブ(勤怠締め業務フロー STEP1 ④「申請書を印刷」)。
///
/// 突合結果と申請書マスタから、様式のある3種類について「提出が必要な人」を出す。
/// 種類を切り替えると対象者の一覧が入れ替わり、チェックを付けた分だけを出力する。
/// 様式はアプリに同梱しているため、画面でテンプレートは指定しない。
/// </summary>
public sealed class ApplicationFormViewModel : ObservableObject
{
    public ApplicationFormViewModel()
    {
        Kinds = ApplicationFormKinds.All.Select(k => new ApplicationFormChoice { Kind = k }).ToList();
        foreach (var choice in Kinds) choice.SelectedChanged += (s, _) => Show((ApplicationFormChoice)s!);
        Kinds[0].IsSelected = true;
    }

    /// <summary>種類の切り替え(タイムカード修正届出書 / 年次有休休暇・欠勤申請書 / 出張届)。</summary>
    public IReadOnlyList<ApplicationFormChoice> Kinds { get; }

    /// <summary>いま表示している種類の対象者。</summary>
    public ObservableCollection<ApplicationTargetRow> Rows { get; } = new();

    private readonly Dictionary<ApplicationFormKind, List<ApplicationTargetRow>> _rowsByKind = new();

    private ApplicationFormKind _selectedKind = ApplicationFormKind.TimeCard;
    public ApplicationFormKind SelectedKind => _selectedKind;

    private string _summaryText = "突合を実行すると、申請書の提出が必要な人がここに出ます。";
    public string SummaryText { get => _summaryText; set => Set(ref _summaryText, value); }

    /// <summary>
    /// 対象者の一覧を作り直す。チェックの状態は作り直しのたびに初期化する。
    ///
    /// 申請書3種類は突合結果から、勤怠管理簿は画面の編集内容(修正した日)から作る。
    /// </summary>
    public void Update(MatchingResult? result, ReportSheet? sheet = null)
    {
        _rowsByKind.Clear();

        var entries = result == null
            ? new List<ApplicationFormEntry>()
            : ApplicationFormTargets.Build(result, result.Masters?.ApplicationForms);

        entries.AddRange(ApplicationFormTargets.BuildLedger(
            sheet, result?.Masters?.JudgementRules.OvertimeMinutes ?? 30));

        foreach (var kind in ApplicationFormKinds.All)
            _rowsByKind[kind] = MakeRows(entries.Where(e => e.Kind == kind));

        foreach (var choice in Kinds) choice.Count = _rowsByKind[choice.Kind].Count;

        Show(Kinds.FirstOrDefault(k => k.Kind == _selectedKind) ?? Kinds[0]);
    }

    /// <summary>
    /// 勤怠管理簿の一覧だけを作り直す(画面でセルを直すたびに対象が変わるため)。
    /// 他の申請書のチェックは触らない。
    /// </summary>
    public void UpdateLedger(ReportSheet? sheet, int overtimeThresholdMinutes = 30)
    {
        if (_rowsByKind.Count == 0) return;

        var kind = ApplicationFormKind.AttendanceLedger;
        _rowsByKind[kind] = MakeRows(ApplicationFormTargets.BuildLedger(sheet, overtimeThresholdMinutes));

        var choice = Kinds.First(k => k.Kind == kind);
        choice.Count = _rowsByKind[kind].Count;

        if (_selectedKind == kind) Show(choice);
        else UpdateSummary();
    }

    private List<ApplicationTargetRow> MakeRows(IEnumerable<ApplicationFormEntry> entries)
    {
        var rows = entries.Select(e => new ApplicationTargetRow { Entry = e }).ToList();

        // チェックの付け外しに合わせて出力枚数の案内を出し直す
        foreach (var row in rows)
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ApplicationTargetRow.Selected)) UpdateSummary();
            };

        return rows;
    }

    /// <summary>チェックが付いている対象(出力する申請書)。</summary>
    public IReadOnlyList<ApplicationFormEntry> CheckedOf(ApplicationFormKind kind)
        => _rowsByKind.TryGetValue(kind, out var rows)
            ? rows.Where(r => r.Selected).Select(r => r.Entry).ToList()
            : new List<ApplicationFormEntry>();

    /// <summary>いま表示している申請書で、チェックが付いている対象。出力するのはこれだけ。</summary>
    public IReadOnlyList<ApplicationFormEntry> CheckedSelected => CheckedOf(_selectedKind);

    /// <summary>表示中の種類のチェックをまとめて付け外しする。</summary>
    public void SelectAll(bool selected)
    {
        foreach (var row in Rows) row.Selected = selected;
        UpdateSummary();
    }

    /// <summary>チェックの数を数え直して案内文を作る(画面のチェック操作のあとに呼ぶ)。</summary>
    public void UpdateSummary()
    {
        if (_rowsByKind.Count == 0)
        {
            SummaryText = "突合を実行すると、申請書の提出が必要な人がここに出ます。";
            return;
        }

        var name = ApplicationFormKinds.NameOf(_selectedKind);
        int total = _rowsByKind[_selectedKind].Count;

        SummaryText = $"出力する申請書 : {name} … チェックを付けた {CheckedSelected.Count} / {total} 名分\n" +
                      "いま選んでいる申請書だけを出力します。様式はアプリに同梱しているものを使います(テンプレートの指定は不要です)。";
    }

    private void Show(ApplicationFormChoice choice)
    {
        _selectedKind = choice.Kind;
        foreach (var other in Kinds) other.IsSelected = other == choice;

        Rows.Clear();
        if (_rowsByKind.TryGetValue(choice.Kind, out var rows))
            foreach (var row in rows) Rows.Add(row);

        UpdateSummary();
    }
}
