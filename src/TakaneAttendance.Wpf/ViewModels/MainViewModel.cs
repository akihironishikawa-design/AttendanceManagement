using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Data;
using Microsoft.Win32;
using TakaneAttendance.Core;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Matching;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Parsing;
using TakaneAttendance.Core.Reporting;

namespace TakaneAttendance.Wpf.ViewModels;

/// <summary>メイン画面のビューモデル。</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly MatchingService _service = new();
    private MatchingResult? _result;
    private ReportSheet? _reportSheet;

    /// <summary>直近に読み書きした保留ファイル。次の保留の既定の保存先に使う。</summary>
    private string _draftPath = "";
    /// <summary>保留してから編集されたか(閉じるときの確認に使う)。</summary>
    private bool _draftDirty;

    public MainViewModel()
    {
        BrowseShiftCommand = new RelayCommand(BrowseShift);
        BrowsePunchCommand = new RelayCommand(BrowsePunch);
        BrowseTemplateCommand = new RelayCommand(BrowseTemplate);
        RunCommand = new RelayCommand(Run, () => CanRun);
        ExportReportCommand = new RelayCommand(ExportReport, () => _reportSheet is { Employees.Count: > 0 });
        ExportApplicationFormListCommand = new RelayCommand(ExportApplicationFormList, () => _result != null);
        ExportUnresolvedCommand = new RelayCommand(ExportUnresolved, () => UnresolvedNames.Count > 0);
        OpenMastersCommand = new RelayCommand(OpenMastersFolder);
        ResetReportEditsCommand = new RelayCommand(
            ResetReportEdits, () => ReportEditedCount > 0 && (_result != null || _draftPath.Length > 0));
        SaveDraftCommand = new RelayCommand(() => SaveDraft(), () => _reportSheet is { Employees.Count: > 0 });
        OpenDraftCommand = new RelayCommand(OpenDraft);

        DetailsView = CollectionViewSource.GetDefaultView(Details);
        DetailsView.Filter = FilterDetail;


        for (int y = DateTime.Today.Year - 3; y <= DateTime.Today.Year + 1; y++) Years.Add(y);
        TargetYear = DateTime.Today.Year;
        TargetMonth = DateTime.Today.Month;

        // 既定のテンプレート位置(実行ファイル配下の templates)
        var defaultTemplate = Path.Combine(AppContext.BaseDirectory, "templates", "出席記録レポート.xlsx");
        if (File.Exists(defaultTemplate)) TemplatePath = defaultTemplate;

        LoadShiftTypes();
    }

    // ================= 入力 =================

    private string _shiftPath = "";
    public string ShiftPath
    {
        get => _shiftPath;
        set { if (Set(ref _shiftPath, value)) LoadShiftSheets(); }
    }

    private string _punchPath = "";
    public string PunchPath
    {
        get => _punchPath;
        set { if (Set(ref _punchPath, value)) LoadPunchSheets(); }
    }

    private string _templatePath = "";
    /// <summary>
    /// 出席記録レポートのテンプレート。
    ///
    /// 指定するのは「3. 帳票出力」タブ(帳票ごとに指定する)で、ここはその値を指す。
    /// 取込画面には出さない。作業番号・部門は従業員マスタから補完できるようになったため、
    /// 突合そのものにテンプレートは要らない。
    /// </summary>
    public string TemplatePath
    {
        get => Export.Reports.FirstOrDefault(r => r.Name == ReportExportViewModel.AttendanceReport)?.TemplatePath
               ?? _templatePath;
        set
        {
            _templatePath = value ?? "";
            var attendance = Export.Reports.FirstOrDefault(r => r.Name == ReportExportViewModel.AttendanceReport);
            if (attendance != null) attendance.TemplatePath = _templatePath;
            OnPropertyChanged();
        }
    }

    public ObservableCollection<string> ShiftSheets { get; } = new();
    public ObservableCollection<string> PunchSheets { get; } = new();

    private string? _selectedShiftSheet;
    public string? SelectedShiftSheet { get => _selectedShiftSheet; set => Set(ref _selectedShiftSheet, value); }

    private string? _selectedPunchSheet;
    public string? SelectedPunchSheet { get => _selectedPunchSheet; set => Set(ref _selectedPunchSheet, value); }

    public ObservableCollection<int> Years { get; } = new();
    public int[] Months { get; } = Enumerable.Range(1, 12).ToArray();

    private int _targetYear;
    public int TargetYear { get => _targetYear; set => Set(ref _targetYear, value); }

    private int _targetMonth;
    public int TargetMonth { get => _targetMonth; set => Set(ref _targetMonth, value); }

    private bool _autoDetectYearMonth = true;
    /// <summary>対象年月をファイルから自動判定する</summary>
    public bool AutoDetectYearMonth { get => _autoDetectYearMonth; set => Set(ref _autoDetectYearMonth, value); }

    private bool _onlyPersonsInShift = true;
    /// <summary>シフト表に載っている社員のみを対象にする</summary>
    public bool OnlyPersonsInShift { get => _onlyPersonsInShift; set => Set(ref _onlyPersonsInShift, value); }

    public bool CanRun => File.Exists(ShiftPath) && File.Exists(PunchPath) && !IsBusy;

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set { if (Set(ref _isBusy, value)) OnPropertyChanged(nameof(CanRun)); } }

    // ================= 結果 =================

    public ObservableCollection<AttendanceDaily> Details { get; } = new();
    public ICollectionView DetailsView { get; }
    public ObservableCollection<UnresolvedName> UnresolvedNames { get; } = new();
    public ObservableCollection<string> Messages { get; } = new();

    private bool _showOnlyIssues = true;
    /// <summary>要確認・警告・エラーだけを表示する</summary>
    public bool ShowOnlyIssues
    {
        get => _showOnlyIssues;
        set { if (Set(ref _showOnlyIssues, value)) DetailsView.Refresh(); }
    }

    private string _searchText = "";
    public string SearchText
    {
        get => _searchText;
        set { if (Set(ref _searchText, value)) DetailsView.Refresh(); }
    }

    private bool FilterDetail(object obj)
    {
        if (obj is not AttendanceDaily d) return false;
        if (ShowOnlyIssues && d.Judgement == Judgement.Normal) return false;
        if (SearchText.Length > 0 &&
            !d.PersonName.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
            !d.Department.Contains(SearchText, StringComparison.OrdinalIgnoreCase) &&
            !d.ResultText.Contains(SearchText, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    // ================= 出席記録レポート(画面編集) =================

    /// <summary>帳票と同じレイアウトの行(社員1名 = シフト行 + 打刻行)。</summary>
    /// <summary>読み込んだ全社員。</summary>
    public ObservableCollection<ReportRow> ReportRows { get; } = new();

    /// <summary>
    /// 一覧に実際に出す社員。絞り込みとページ分けの結果を入れる(仕様書 v3.0 第8.2章)。
    /// ページ送りをするため、ICollectionView のフィルタではなく自前の一覧にしている。
    /// </summary>
    public ObservableCollection<ReportRow> ReportRowsView { get; } = new();

    /// <summary>画面で編集中の帳票モデル。出力時はこの内容をそのまま書き出す。</summary>
    public ReportSheet? ReportSheet => _reportSheet;

    /// <summary>日列の本数が対象月で変わるため、列は画面側で作り直してもらう。</summary>
    public event EventHandler? ReportSheetChanged;

    private IReadOnlyList<ShiftTypeEntry> _shiftTypeChoices = Array.Empty<ShiftTypeEntry>();
    /// <summary>セル編集ポップアップに出す勤務区分の一覧(勤務区分マスタ)。</summary>
    public IReadOnlyList<ShiftTypeEntry> ShiftTypeChoices => _shiftTypeChoices;

    /// <summary>マスタは実行中に書き換えられることがあるため、突合のたびに読み直す。</summary>
    private void LoadShiftTypes()
    {
        try
        {
            _shiftTypeChoices = ShiftTypeMaster
                .Load(Path.Combine(MasterSet.DefaultDirectory, MasterSet.ShiftTypeFileName)).All;
        }
        catch (Exception ex)
        {
            _shiftTypeChoices = ShiftTypeMaster.CreateDefault().All;
            StatusText = $"勤務区分マスタを読めません({ex.Message})。既定の区分を使用します。";
        }
    }

    /// <summary>
    /// マスタ編集画面で保存されたあと、画面が持っているマスタを読み直す。
    /// 突合結果そのものは、次に「突合実行」を押したときに作り直される。
    /// </summary>
    public void ReloadMasters()
    {
        // 読み込みに失敗した場合の理由を残すため、先に既定のメッセージを入れておく
        StatusText = "マスタを読み直しました。突合結果へは、次に「突合実行」を押したときから反映されます。";
        LoadShiftTypes();
    }

    // ================= ドラッグ＆ドロップ =================

    /// <summary>ドロップ先。<see cref="DropSlot.Auto"/> はファイルの中身から振り分ける。</summary>
    public enum DropSlot { Auto, Shift, Punch, Template }

    /// <summary>
    /// ドロップされたファイルを取り込む。
    ///
    /// 入力欄の上に落とした場合はその欄へ、それ以外の場所に落とした場合は
    /// ファイルの中身を見て シフト表 / 打刻データ / 帳票テンプレート に振り分ける。
    /// 判別できないファイルは、空いている欄(シフト表 → 打刻データ)に入れる。
    /// </summary>
    public void ApplyDroppedFiles(IReadOnlyList<string> paths, DropSlot slot)
    {
        var files = paths.Where(File.Exists).ToList();
        if (files.Count == 0) return;

        // 保留ファイルは、どこに落としても「保留を開く」として扱う
        if (files.FirstOrDefault(IsDraftFile) is { } draft)
        {
            if (!ConfirmDiscardEdits("保留ファイルを開くと", allowSave: false)) return;
            OpenDraft(draft);
            if (files.Count > 1)
                StatusText += $" / 同時に落とされた {files.Count - 1} 件は取り込んでいません";
            return;
        }

        var ignored = files.Where(f => !WorkbookClassifier.IsExcelFile(f)).ToList();
        files.RemoveAll(f => !WorkbookClassifier.IsExcelFile(f));

        if (files.Count == 0)
        {
            StatusText = "Excel ファイル(.xls / .xlsx)または保留ファイル(.kintai.json)をドロップしてください。" +
                         $"({Path.GetFileName(ignored[0])})";
            return;
        }

        var applied = new List<string>();

        if (slot != DropSlot.Auto)
        {
            Assign(slot, files[0], applied);
            if (files.Count > 1)
                applied.Add($"※ 2件目以降は取り込んでいません({files.Count - 1} 件)");
        }
        else
        {
            foreach (var file in files)
            {
                var kind = WorkbookClassifier.Detect(file);
                var target = kind switch
                {
                    WorkbookKind.Shift => DropSlot.Shift,
                    WorkbookKind.Punch => DropSlot.Punch,
                    WorkbookKind.ReportTemplate => DropSlot.Template,
                    _ => EmptySlot()
                };

                if (target == DropSlot.Auto)
                {
                    applied.Add($"※ {Path.GetFileName(file)} は種類を判別できませんでした。欄へ直接ドロップしてください");
                    continue;
                }
                Assign(target, file, applied);
            }
        }

        foreach (var f in ignored) applied.Add($"※ {Path.GetFileName(f)} は Excel ファイルではないため取り込んでいません");

        StatusText = applied.Count > 0
            ? string.Join(" / ", applied)
            : "取り込めるファイルがありませんでした。";
    }

    /// <summary>判別できなかったファイルの入れ先。空いている欄を順に使う。</summary>
    private DropSlot EmptySlot()
    {
        if (ShiftPath.Length == 0) return DropSlot.Shift;
        if (PunchPath.Length == 0) return DropSlot.Punch;
        return DropSlot.Auto;   // 空きなし
    }

    private void Assign(DropSlot slot, string path, List<string> applied)
    {
        switch (slot)
        {
            case DropSlot.Shift:
                ShiftPath = path;
                applied.Add($"シフト表: {Path.GetFileName(path)}");
                break;

            case DropSlot.Punch:
                PunchPath = path;
                applied.Add($"打刻データ: {Path.GetFileName(path)}");
                break;

            case DropSlot.Template:
                // 帳票の書き出しは .xlsx だけを扱う(書式を保ったまま書き込むため)
                if (!Path.GetExtension(path).Equals(".xlsx", StringComparison.OrdinalIgnoreCase))
                {
                    applied.Add($"※ 帳票テンプレートは .xlsx を指定してください({Path.GetFileName(path)})");
                    break;
                }
                // 取込画面に欄は無いが、落とされたテンプレートは
                // 「3. 帳票出力」タブの出席記録レポートに入れておく
                TemplatePath = path;
                applied.Add($"帳票テンプレート(出席記録レポート): {Path.GetFileName(path)}" +
                            "　→「3. 帳票出力」タブで確認できます");
                break;
        }
    }

    // ================= 氏名未解決の登録 =================

    private UnresolvedName? _selectedUnresolved;
    /// <summary>「氏名未解決」タブで選んでいる行。</summary>
    public UnresolvedName? SelectedUnresolved
    {
        get => _selectedUnresolved;
        set => Set(ref _selectedUnresolved, value);
    }

    /// <summary>
    /// 別名マスタの「正式氏名」の候補。
    /// 打刻データ側の社員(作業番号を持つため正式氏名として信頼できる)から作る。
    /// </summary>
    public IReadOnlyList<CanonicalCandidate> CanonicalCandidates()
    {
        if (_result == null) return Array.Empty<CanonicalCandidate>();

        return _result.Details
            .Select(d => d.Person)
            .Where(p => p.IsResolved && !string.IsNullOrWhiteSpace(p.EmployeeNo))
            .GroupBy(p => p.Key)
            .Select(g => g.First())
            .Select(p => new CanonicalCandidate(p.DisplayName, p.EmployeeNo ?? "", p.Department ?? ""))
            .ToList();
    }

    /// <summary>
    /// 未解決の氏名を別名マスタ(name_alias.xml)へ登録する。
    /// 同じ表記が既に登録されている場合は、その行の正式氏名を書き換える。
    /// </summary>
    /// <returns>登録できたか。失敗した理由は <see cref="StatusText"/> と処理ログに残す。</returns>
    public bool RegisterAlias(UnresolvedName unresolved, string canonical)
    {
        var path = Path.Combine(MasterSet.DefaultDirectory, MasterSet.AliasFileName);
        var note = $"画面から登録 {DateTime.Now:yyyy-MM-dd} {unresolved.Origin}".Trim();

        try
        {
            MasterEditor.UpsertAlias(path, new AliasEntry(unresolved.SourceName, canonical.Trim(), note));
        }
        catch (Exception ex)
        {
            StatusText = $"別名マスタに登録できません: {ex.Message}";
            Messages.Add($"[マスタ] 登録失敗 {unresolved.SourceName}: {ex.Message}");
            return false;
        }

        StatusText = canonical.Trim().Length > 0
            ? $"別名マスタに登録しました: 「{unresolved.SourceName}」→「{canonical.Trim()}」"
            : $"別名マスタに登録しました: 「{unresolved.SourceName}」(正式氏名そのものとして登録)";
        Messages.Add($"[マスタ] {StatusText}");
        LoadShiftTypes();
        return true;
    }

    private string _reportSearchText = "";
    public string ReportSearchText
    {
        get => _reportSearchText;
        set
        {
            if (!Set(ref _reportSearchText, value)) return;
            _currentPage = 1;
            RefreshReportView();
        }
    }

    public int ReportEditedCount => _reportSheet?.EditedCellCount ?? 0;

    private string _reportSummaryText = "突合を実行すると、出席記録レポートと同じ形式で表示します。セルを直接編集できます。";
    public string ReportSummaryText { get => _reportSummaryText; set => Set(ref _reportSummaryText, value); }

    /// <summary>突合結果から帳票モデルを組み立て直す(画面の編集は破棄される)。</summary>
    /// <summary>
    /// 突合結果から、帳票と同じレイアウトの編集用モデルを組み立てる。
    /// </summary>
    /// <param name="draft">
    /// 保留を開いた場合の、保留ファイルの帳票。組み立て直した内容にその編集を重ねる。
    /// </param>
    /// <param name="draftPath">重ねた保留ファイルの場所(次の保存先の表示に使う)。</param>
    private void BuildReportSheet(ReportSheet? draft = null, string? draftPath = null)
    {
        ReportSheet? sheet = null;

        // 突合結果から作り直した内容は、もう保留ファイルの内容ではない
        _draftPath = draftPath ?? "";

        if (_result != null)
        {
            var template = File.Exists(TemplatePath) ? TemplatePath : null;
            sheet = ReportSheetBuilder.Build(_result, template);
            foreach (var m in sheet.Messages) Messages.Add("[帳票] " + m);

            if (draft != null)
            {
                var applied = sheet.ApplyDraftEdits(draft, CreateJudge());
                Messages.Add($"[保留] {applied.Describe()}。");
                if (applied.MissingEmployees.Count > 0)
                    Messages.Add("[保留] 編集していた社員が今回の突合に居ませんでした(編集は取り込めていません): " +
                                 string.Join(", ", applied.MissingEmployees));
                if (draft.History.Count > 0)
                    Messages.Add($"[保留] 修正履歴 {draft.History.Count} 件を引き継ぎました。");
            }
        }

        SetReportSheet(sheet);
    }

    /// <summary>
    /// 画面が持つ帳票モデルを差し替える(突合結果から作った場合と、保留ファイルから戻した場合の共通処理)。
    /// </summary>
    private void SetReportSheet(ReportSheet? sheet, ReportJudge? judge = null)
    {
        foreach (var row in ReportRows) row.CellChanged -= OnReportCellChanged;
        ReportRows.Clear();
        _reportSheet = sheet;

        if (sheet != null)
        {
            judge ??= CreateJudge();

            int order = 0;
            foreach (var block in sheet.Employees)
            {
                var row = new ReportRow(block, order++, sheet, judge);
                row.CellChanged += OnReportCellChanged;
                ReportRows.Add(row);
            }
        }

        // 組み立て直した直後は「保留してから何も編集していない」状態にする
        _draftDirty = false;

        ReportSheetChanged?.Invoke(this, EventArgs.Empty);
        _currentPage = 1;
        RefreshReportView();
        UpdateReportSummary();

        // 出力先の既定はシフト表と同じフォルダにしておく
        if (Export.OutputDirectory.Length == 0 && ShiftPath.Length > 0)
            Export.OutputDirectory = Path.GetDirectoryName(ShiftPath) ?? "";
        RefreshExportPreview();
    }

    /// <summary>編集したセルをその場で判定し直すための判定器。突合と同じマスタ・条件を使う。</summary>
    private ReportJudge CreateJudge() => new(
        MasterSet.Load(MasterSet.DefaultDirectory),
        new MatchingOptions { OnlyPersonsInShift = OnlyPersonsInShift });

    private void OnReportCellChanged(object? sender, EventArgs e)
    {
        _draftDirty = true;
        UpdateReportSummary();

        // 勤怠管理簿は「画面で修正した日」が対象のため、編集のたびに一覧を作り直す
        // (他の申請書のチェックは残す)
        Forms.UpdateLedger(_reportSheet, _result?.Masters?.JudgementRules.OvertimeMinutes ?? 30);
    }

    private void UpdateReportSummary()
    {
        OnPropertyChanged(nameof(ReportEditedCount));

        if (_reportSheet == null || _reportSheet.Employees.Count == 0)
        {
            ReportSummaryText = "突合を実行すると、出席記録レポートと同じ形式で表示します。セルを直接編集できます。";
            return;
        }

        var text = $"{_reportSheet.Year}年{_reportSheet.Month}月 / シフト表の {_reportSheet.Employees.Count} 名" +
                   $" · 要確認 {_reportSheet.AttentionCellCount} 日";
        if (ReportEditedCount > 0) text += $" · 編集済み {ReportEditedCount} セル";
        if (_draftPath.Length > 0)
            text += $" · 保留: {Path.GetFileName(_draftPath)}{(_draftDirty ? "(保留後に編集あり)" : "")}";
        else if (_draftDirty)
            text += " · 未保留";
        ReportSummaryText = text;
    }

    /// <summary>
    /// 画面の編集を破棄して、元の内容に戻す。
    /// 戻し先は、突合から作った画面なら突合結果、保留から再開した画面なら保留ファイルの内容。
    /// </summary>
    private void ResetReportEdits()
    {
        // 保留から再開した場合は突合結果を持たないため、保留ファイルを読み直す
        bool fromDraft = _result == null;
        if (fromDraft && _draftPath.Length == 0) return;

        var message = fromDraft
            ? $"保留したあとの編集を破棄して、保留ファイルの内容に戻します。\n\n{_draftPath}\n\nよろしいですか?"
            : $"画面で編集した {ReportEditedCount} セルを破棄して、突合結果の内容に戻します。よろしいですか?";

        var answer = System.Windows.MessageBox.Show(
            message, "編集内容を元に戻す",
            System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.OK) return;

        if (fromDraft)
        {
            OpenDraft(_draftPath);
            return;   // 状況は OpenDraft がステータスに出す
        }

        BuildReportSheet();
        StatusText = "出席記録レポートの編集内容を元に戻しました。";
    }

    // ================= 保留(作業途中の保存と再開) =================

    /// <summary>保留してから編集された内容があるか(閉じるときの確認に使う)。</summary>
    public bool HasUnsavedEdits => _draftDirty && ReportEditedCount > 0;

    /// <summary>
    /// 編集中の帳票を一時保存する。
    ///
    /// 締めの作業中は何度も保存するため、保存先は毎回聞かず
    /// 決まった1ファイル(作業データ\勤怠突合状況.xlsx)へ上書きする。
    /// 上書き前の内容は同じ場所に .bak として1世代だけ残す。
    /// </summary>
    /// <returns>書き出せたか。</returns>
    public bool SaveDraft()
    {
        if (_reportSheet == null) return false;

        var path = DraftExcelFile.DefaultPath;

        // 別の月の作業が入っている場合だけ、上書きしてよいかを確かめる
        if (DraftExcelFile.PeekPeriod(path) is { } saved &&
            (saved.Year != _reportSheet.Year || saved.Month != _reportSheet.Month))
        {
            var answer = System.Windows.MessageBox.Show(
                $"一時保存ファイルには {saved.Year}年{saved.Month}月 の作業が入っています。\n" +
                $"{_reportSheet.Year}年{_reportSheet.Month}月 の内容で上書きしますか?\n\n" +
                $"{path}\n\n上書き前の内容は .bak として残ります。",
                "一時保存", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
            if (answer != System.Windows.MessageBoxResult.Yes) return false;
        }

        return SaveDraftTo(path);
    }

    /// <summary>
    /// 保留ファイル(作業データ\勤怠突合状況.xlsx)があるか。
    /// ある場合は「保留を開く」を目立たせて、続きから再開できることを知らせる。
    /// </summary>
    public bool HasDraftFile => File.Exists(DraftExcelFile.DefaultPath);

    /// <summary>保留ファイルの有無を画面へ知らせ直す。</summary>
    public void RefreshDraftState() => OnPropertyChanged(nameof(HasDraftFile));

    /// <summary>保留ファイルを書き出す。閉じるときの確認からも呼ぶ。</summary>
    /// <returns>書き出せたか。</returns>
    public bool SaveDraftTo(string path)
    {
        if (_reportSheet == null) return false;

        bool isJson = path.EndsWith(DraftFile.Extension, StringComparison.OrdinalIgnoreCase);
        if (!isJson && !path.EndsWith(".xlsx", StringComparison.OrdinalIgnoreCase)) path += ".xlsx";

        try
        {
            // 入力条件・修正履歴も一緒に保存し、開いたときに経緯が分かるようにする
            if (isJson)
                DraftFile.Save(path, DraftDocument.FromSheet(_reportSheet, CurrentDraftInputs(), Messages));
            else
                DraftExcelFile.Save(path, _reportSheet, CurrentDraftInputs());
        }
        catch (Exception ex)
        {
            StatusText = $"保留できません: {ex.Message}";
            Messages.Add($"[保留] 保存に失敗しました: {ex.Message}");
            return false;
        }

        _draftPath = path;
        _draftDirty = false;
        StatusText = $"一時保存しました(編集 {ReportEditedCount} セル / 修正履歴 {History?.Count ?? 0} 件)　{path}";
        Messages.Add($"[一時保存] 保存しました: {path}");
        UpdateReportSummary();
        RefreshDraftState();
        return true;
    }

    /// <summary>Excel 形式の一時保存ファイルを開く(仕様書 v3.0 第8.4章)。</summary>
    private void OpenDraftExcel(string path)
    {
        DraftExcelFile.LoadResult loaded;
        try
        {
            LoadShiftTypes();
            loaded = DraftExcelFile.Load(path, CreateJudge());
        }
        catch (Exception ex)
        {
            StatusText = $"一時保存ファイルを開けません: {ex.Message}";
            Messages.Add($"[一時保存] 読み込みに失敗しました({Path.GetFileName(path)}): {ex.Message}");
            return;
        }

        RestoreInputs(loaded.Inputs);

        if (!Years.Contains(loaded.Sheet.Year) && loaded.Sheet.Year > 0)
        {
            int at = 0;
            while (at < Years.Count && Years[at] < loaded.Sheet.Year) at++;
            Years.Insert(at, loaded.Sheet.Year);
        }
        if (loaded.Sheet.Year > 0) TargetYear = loaded.Sheet.Year;
        if (loaded.Sheet.Month > 0) TargetMonth = loaded.Sheet.Month;

        Messages.Clear();
        foreach (var m in loaded.Messages) Messages.Add("[一時保存] " + m);

        // 元のシフト表・打刻データが残っていれば、そのまま突合まで済ませる
        if (RunFromDraft(loaded.Sheet, path)) return;

        // 突合できない場合は帳票だけを戻す(明細・未解決の一覧は空になる)
        _result = null;
        Details.Clear();
        UnresolvedNames.Clear();
        SelectedUnresolved = null;

        SetReportSheet(loaded.Sheet);
        _draftPath = path;

        foreach (var n in new[] { nameof(PersonCount), nameof(NormalCount), nameof(LateCount),
                                  nameof(EarlyLeaveCount), nameof(EarlyInCount), nameof(OvertimeCount),
                                  nameof(ReviewCount), nameof(ExcludedCount), nameof(DetailCount) })
            OnPropertyChanged(n);
        DetailsView.Refresh();

        SummaryText =
            $"{loaded.Sheet.Year}年{loaded.Sheet.Month}月 / 保留していた {loaded.Sheet.Employees.Count} 名。" +
            "シフト表・打刻データが見つからないため突合はしていません" +
            "(上の件数と「突合結果」「氏名未解決」タブは空のままです)。";
        StatusText = $"一時保存ファイルを開きました。突合はしていません(社員 {loaded.Sheet.Employees.Count} 名 / " +
                     $"修正履歴 {loaded.Sheet.History.Count} 件" +
                     (loaded.ChangedJudgements > 0 ? $" / 判定が変わった {loaded.ChangedJudgements} セル" : "") + ")";
    }

    /// <summary>
    /// 一時保存の続きから再開する。
    ///
    /// ファイルは1つに決まっているため、開く場所も聞かない。
    /// 別のファイルを開きたい場合は、画面へドラッグ＆ドロップしてください。
    /// </summary>
    private void OpenDraft()
    {
        var path = DraftExcelFile.DefaultPath;
        if (!File.Exists(path))
        {
            StatusText = $"一時保存ファイルがありません({path})。先に「保留(作業途中を保存)」を押してください。";
            return;
        }

        if (!ConfirmDiscardEdits("保留を開くと", allowSave: false)) return;
        OpenDraft(path);
    }

    /// <summary>
    /// 保留ファイルを開いて、続きから編集できるようにする。
    ///
    /// 保留したときのシフト表・打刻データが残っていれば、そのまま突合まで行い、
    /// 保留していた編集をその結果に重ねる(<see cref="RunFromDraft"/>)。
    /// 元ファイルが見つからない場合だけ、保留ファイルの帳票をそのまま戻す。
    /// いずれの場合も判定は現在のマスタで計算し直すため、保留中にマスタを直した内容で再開できる。
    /// </summary>
    public void OpenDraft(string path)
    {
        // Excel 形式(仕様書 8.4)と、従来の .kintai.json の両方を開けるようにする
        if (!path.EndsWith(DraftFile.Extension, StringComparison.OrdinalIgnoreCase))
        {
            OpenDraftExcel(path);
            return;
        }

        DraftDocument document;
        try
        {
            document = DraftFile.Load(path);
        }
        catch (Exception ex)
        {
            StatusText = $"一時保存ファイルを開けません: {ex.Message}";
            Messages.Add($"[一時保存] 読み込みに失敗しました({Path.GetFileName(path)}): {ex.Message}");
            return;
        }

        RestoreInputs(document.Inputs);

        // 対象年月は保留ファイルの帳票に合わせる(一覧に無い年は追加する)
        if (!Years.Contains(document.Year))
        {
            int at = 0;
            while (at < Years.Count && Years[at] < document.Year) at++;
            Years.Insert(at, document.Year);
        }
        TargetYear = document.Year;
        TargetMonth = document.Month;

        Messages.Clear();
        foreach (var m in document.Messages) Messages.Add(m);

        LoadShiftTypes();
        var judge = CreateJudge();
        var sheet = document.ToSheet(judge, out int changedJudgements);

        // 元のシフト表・打刻データが残っていれば、そのまま突合まで済ませる
        if (RunFromDraft(sheet, path)) return;

        // 突合できない場合は帳票だけを戻す(明細・未解決の一覧は空になる)
        _result = null;
        Details.Clear();
        UnresolvedNames.Clear();
        SelectedUnresolved = null;

        SetReportSheet(sheet, judge);

        _draftPath = path;

        foreach (var n in new[] { nameof(PersonCount), nameof(NormalCount), nameof(LateCount),
                                  nameof(EarlyLeaveCount), nameof(EarlyInCount), nameof(OvertimeCount),
                                  nameof(ReviewCount), nameof(ExcludedCount), nameof(DetailCount) })
            OnPropertyChanged(n);
        DetailsView.Refresh();

        SummaryText =
            $"保留ファイルを開きました。{document.Year}年{document.Month}月 / {sheet.Employees.Count} 名 " +
            $"(編集済み {sheet.EditedCellCount} セル)。" +
            "シフト表・打刻データが見つからないため突合はしていません" +
            "(「突合結果」「氏名未解決」タブは空のままです)。";
        StatusText = $"保留を開きました(保存日時 {document.SavedAt}): {path}";

        Messages.Add($"[保留] 読み込みました: {path}(保存日時 {document.SavedAt} / 編集 {sheet.EditedCellCount} セル)");
        if (changedJudgements > 0)
            Messages.Add($"[保留] 現在のマスタで判定し直した結果、{changedJudgements} セルの判定が保留時と変わりました。");

        UpdateReportSummary();
    }

    /// <summary>
    /// 保留を開いたあと、そのときの入力条件で突合をやり直す。
    ///
    /// 保留ファイルに入っているのは編集中の帳票だけのため、開いただけでは
    /// 「突合結果」「氏名未解決」タブと上部の件数、帳票出力タブが空のままになる。
    /// 元のシフト表・打刻データが残っていれば読み直して突合し、
    /// 保留していた編集をその結果に重ねることで、締めの続きをそのまま行えるようにする。
    ///
    /// 元ファイルが差し替わっていた場合は、新しい内容に編集が重なる。
    /// 重ねられなかった編集は処理ログに出す。
    /// </summary>
    /// <returns>突合まで済んだか。false のときは呼び出し側が帳票だけを戻す。</returns>
    private bool RunFromDraft(ReportSheet draftSheet, string draftPath)
    {
        if (MissingInputFile("シフト表", ShiftPath) || MissingInputFile("打刻データ", PunchPath))
            return false;

        // 突合は処理ログを作り直すため、ここまでの一時保存のメッセージを控えて戻す
        var carried = Messages.ToList();

        // 突合に失敗した場合に前回の結果が残らないようにしてから実行する
        _result = null;
        Run(draftSheet, draftPath);
        if (_result == null) return false;

        for (int i = 0; i < carried.Count; i++) Messages.Insert(i, carried[i]);
        Messages.Insert(carried.Count,
            "[保留] 保留を開いたので、シフト表・打刻データを読み直して突合まで行いました。" +
            "保留していた編集は、その結果に重ねています。");

        var name = Path.GetFileName(draftPath);
        SummaryText += $" ※保留({name})の続きです";
        StatusText = $"保留を開いて突合し直しました(編集 {ReportEditedCount} セル / " +
                     $"修正履歴 {History?.Count ?? 0} 件)　{draftPath}";
        return true;
    }

    /// <summary>入力ファイルが無いことを処理ログに出す。</summary>
    private bool MissingInputFile(string label, string path)
    {
        if (path.Length > 0 && File.Exists(path)) return false;

        Messages.Add($"[保留] {label}が見つからないため、突合はしていません" +
                     $"({(path.Length == 0 ? "未設定" : path)})。" +
                     "ファイルを指定して「突合実行」を押すと、保留した編集は破棄されます。");
        return true;
    }

    /// <summary>
    /// 編集内容が失われる操作の前に確認する。
    /// 保留していない編集がある場合だけ尋ね、その場で保留できるようにする。
    /// </summary>
    /// <returns>操作を続けてよいか。</returns>
    /// <param name="allowSave">
    /// 先に保留してから続ける選択肢を出すか。
    /// 保留を開くときは false にする(開く先と同じファイルへ保存してしまい、
    /// 開こうとしていた内容が消えるため)。
    /// </param>
    public bool ConfirmDiscardEdits(string whatHappens, bool allowSave = true)
    {
        // 保留済みで、そのあと編集していない場合は失われるものが無いため尋ねない
        if (!HasUnsavedEdits) return true;

        if (!allowSave)
        {
            var confirm = System.Windows.MessageBox.Show(
                $"{whatHappens}、出席記録レポートで編集した {ReportEditedCount} セルは破棄されます。" +
                "\n\n保留ファイルの内容で開き直します。よろしいですか?",
                "編集内容の確認",
                System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Warning);
            return confirm == System.Windows.MessageBoxResult.OK;
        }

        var answer = System.Windows.MessageBox.Show(
            $"{whatHappens}、出席記録レポートで編集した {ReportEditedCount} セルは破棄されます。\n\n" +
            "「はい」  : 先に保留(作業途中を保存)してから続けます\n" +
            "「いいえ」: 保留せずに続けます\n" +
            "「キャンセル」: 何もしません",
            "編集内容の確認",
            System.Windows.MessageBoxButton.YesNoCancel, System.Windows.MessageBoxImage.Warning);

        return answer switch
        {
            System.Windows.MessageBoxResult.Yes => SaveDraft(),   // 保留を取り消した場合は続行しない
            System.Windows.MessageBoxResult.No => true,
            _ => false
        };
    }

    /// <summary>保留ファイルかどうか(ドロップされたファイルの振り分けに使う)。</summary>
    public static bool IsDraftFile(string path)
        => path.EndsWith(DraftFile.Extension, StringComparison.OrdinalIgnoreCase);

    private DraftInputs CurrentDraftInputs() => new()
    {
        ShiftPath = ShiftPath,
        ShiftSheetName = SelectedShiftSheet ?? "",
        PunchPath = PunchPath,
        PunchSheetName = SelectedPunchSheet ?? "",
        TemplatePath = TemplatePath,
        MastersDirectory = MasterSet.DefaultDirectory,
        AutoDetectYearMonth = AutoDetectYearMonth,
        OnlyPersonsInShift = OnlyPersonsInShift
    };

    /// <summary>
    /// 保留したときの入力条件を画面に戻す。
    /// パスを入れるとシート一覧が読み直され、シートが自動選択されるため、
    /// そのあとで保留時のシート名に上書きする(一覧に無い場合は自動選択のまま)。
    /// </summary>
    private void RestoreInputs(DraftInputs inputs)
    {
        ShiftPath = inputs.ShiftPath;
        if (ShiftSheets.Contains(inputs.ShiftSheetName)) SelectedShiftSheet = inputs.ShiftSheetName;

        PunchPath = inputs.PunchPath;
        if (PunchSheets.Contains(inputs.PunchSheetName)) SelectedPunchSheet = inputs.PunchSheetName;

        // テンプレートは既定値が入っているため、保留側に指定がある場合だけ上書きする
        if (inputs.TemplatePath.Length > 0) TemplatePath = inputs.TemplatePath;

        AutoDetectYearMonth = inputs.AutoDetectYearMonth;
        OnlyPersonsInShift = inputs.OnlyPersonsInShift;
    }

    /// <summary>保存先に保留ファイルの拡張子(.kintai.json)を付ける。</summary>
    private static string WithDraftExtension(string path)
    {
        if (path.EndsWith(DraftFile.Extension, StringComparison.OrdinalIgnoreCase)) return path;

        if (path.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) path = path[..^".json".Length];
        if (path.EndsWith(".kintai", StringComparison.OrdinalIgnoreCase)) path = path[..^".kintai".Length];
        return path + DraftFile.Extension;
    }

    private string _summaryText = "ファイルを選択して「突合実行」を押してください。";
    public string SummaryText { get => _summaryText; set => Set(ref _summaryText, value); }

    private string _statusText = "準備完了";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    public int PersonCount     => _result?.PersonCount     ?? 0;
    public int NormalCount     => _result?.NormalCount     ?? 0;
    public int LateCount       => _result?.LateCount       ?? 0;
    public int EarlyLeaveCount => _result?.EarlyLeaveCount ?? 0;
    public int EarlyInCount    => _result?.EarlyInCount    ?? 0;
    public int OvertimeCount   => _result?.OvertimeCount   ?? 0;
    public int ReviewCount     => _result?.ReviewCount     ?? 0;
    public int ExcludedCount   => _result?.ExcludedCount   ?? 0;
    public int DetailCount     => _result?.Details.Count   ?? 0;

    // ================= コマンド =================

    public RelayCommand BrowseShiftCommand { get; }
    public RelayCommand BrowsePunchCommand { get; }
    public RelayCommand BrowseTemplateCommand { get; }
    public RelayCommand RunCommand { get; }
    /// <summary>画面での修正履歴(仕様書 v3.0 第18.1章)。突合をやり直すと帳票と一緒に破棄される。</summary>
    public EditHistory? History => _reportSheet?.History;

    // ================= 帳票出力(仕様書 v3.0 第9章) =================

    /// <summary>帳票出力タブの状態(選択・テンプレート・出力先・履歴)。</summary>
    public ReportExportViewModel Export { get; } = new();

    /// <summary>
    /// 出勤簿を出力する(統合仕様書 v3.0 第16章)。
    ///
    /// 様式はアプリに同梱しているものを使うため、テンプレートの指定は要らない。
    /// 画面に表示されている内容(編集後の勤務区分)をそのまま書き出す。
    /// </summary>
    public void ExportAttendanceBook()
    {
        if (_result == null || _reportSheet == null)
        {
            StatusText = "先に突合を実行してください。";
            return;
        }

        if (CreateOutputFolder() is not { } folder) return;

        var period = $"{_result.TargetYear}年{_result.TargetMonth}月";
        var path = Path.Combine(folder, $"出勤簿_{_result.TargetYear}{_result.TargetMonth:00}.xls");

        try
        {
            var written = AttendanceBookWriter.Write(
                _reportSheet, BundledTemplate.PathOf(BundledTemplate.AttendanceBookFile), path);
            foreach (var m in written.Messages) Messages.Add($"[出勤簿] {m}");

            Export.AddHistory("出勤簿", period, path, written.Success,
                              written.Success ? $"{written.WrittenEmployees} 名 / {written.WrittenCells} セル" : "出力できませんでした");

            StatusText = written.Success
                ? $"出勤簿を出力しました({written.WrittenEmployees} 名): {path}"
                : "出勤簿を出力できませんでした。処理ログを確認してください。";
        }
        catch (Exception ex)
        {
            Messages.Add($"[出勤簿] 出力に失敗しました: {ex.Message}");
            Export.AddHistory("出勤簿", period, path, false, ex.Message);
            StatusText = $"出勤簿の出力エラー: {ex.Message}";
        }

        OpenFolder(folder);
    }

    /// <summary>
    /// パート・アルバイト給与計算表を出力する(統合仕様書 v3.0 第16章)。
    ///
    /// 対象はパート・アルバイトマスタに登録されている方(1人1シート)。
    /// 出社・退社の打刻と、丸め後の拘束時間から求めた休憩を書き込み、
    /// 労働時間・時間外・金額は様式側の数式で計算されます。
    /// </summary>
    public void ExportPartTimePayroll()
    {
        if (_result == null)
        {
            StatusText = "先に突合を実行してください。";
            return;
        }

        if (CreateOutputFolder() is not { } folder) return;

        var period = $"{_result.TargetYear}年{_result.TargetMonth}月";
        var path = Path.Combine(folder, $"パートアルバイト給与計算表_{_result.TargetYear}{_result.TargetMonth:00}.xlsx");

        try
        {
            var written = PartTimePayrollWriter.Write(
                _result, BundledTemplate.PathOf(BundledTemplate.PartTimePayrollFile), path,
                _result.Masters?.BreakRules ?? new Core.Masters.BreakRuleMaster());
            foreach (var m in written.Messages) Messages.Add($"[給与計算表] {m}");

            Export.AddHistory("パート・アルバイト給与計算表", period, path, written.Success,
                              written.Success ? $"{written.WrittenEmployees} 名" : "出力できませんでした");

            StatusText = written.Success
                ? $"パート・アルバイト給与計算表を出力しました({written.WrittenEmployees} 名): {path}"
                : "パート・アルバイト給与計算表を出力できませんでした。処理ログを確認してください。";
        }
        catch (Exception ex)
        {
            Messages.Add($"[給与計算表] 出力に失敗しました: {ex.Message}");
            Export.AddHistory("パート・アルバイト給与計算表", period, path, false, ex.Message);
            StatusText = $"給与計算表の出力エラー: {ex.Message}";
        }

        OpenFolder(folder);
    }

    /// <summary>申請書出力タブの状態(種類・対象者・チェック)。</summary>
    public ApplicationFormViewModel Forms { get; } = new();

    /// <summary>
    /// 画面で選んでいる申請書を、チェックを付けた人の分だけ出力する(勤怠締め業務フロー STEP1 ④)。
    ///
    /// 様式はアプリに同梱しているものを使うため、テンプレートの指定は要らない。
    /// 1つのファイルにまとめ、1シートに2枚ずつ並べて出す。
    /// </summary>
    public void ExportApplicationForms()
    {
        if (_result == null)
        {
            StatusText = "先に突合を実行してください。";
            return;
        }

        var kind = Forms.SelectedKind;
        var name = ApplicationFormKinds.NameOf(kind);
        var entries = Forms.CheckedSelected;
        if (entries.Count == 0)
        {
            StatusText = $"{name} に出力対象のチェックが付いていません。";
            return;
        }

        var folder = CreateOutputFolder();
        if (folder == null) return;

        var period = $"{_result.TargetYear}年{_result.TargetMonth}月";
        var path = Path.Combine(folder, ApplicationFormKinds.FileNameFor(kind, _result.TargetYear, _result.TargetMonth));

        try
        {
            // 勤怠管理簿だけ様式が違う(社員1名につき1シート・複数日を1枚に書く)
            var written = kind == ApplicationFormKind.AttendanceLedger
                ? AttendanceLedgerWriter.Write(entries.Select(e => e.Ledger!).Where(l => l != null).ToList(),
                                               ApplicationFormTemplates.PathOf(kind), path,
                                               _result.TargetYear, _result.TargetMonth)
                : ApplicationFormWriter.Write(kind, entries, ApplicationFormTemplates.PathOf(kind),
                                              path, DateOnly.FromDateTime(DateTime.Today));
            foreach (var m in written.Messages) Messages.Add($"[{name}] {m}");

            Export.AddHistory(name, period, path, written.Success,
                              written.Success ? $"{written.WrittenEmployees} 名 / {written.WrittenCells} 枚" : "出力できませんでした");

            StatusText = written.Success
                ? $"{name} を出力しました({written.WrittenEmployees} 名 / {written.WrittenCells} 枚): {path}"
                : $"{name} を出力できませんでした。処理ログを確認してください。";
        }
        catch (Exception ex)
        {
            Messages.Add($"[{name}] 出力に失敗しました: {ex.Message}");
            Export.AddHistory(name, period, path, false, ex.Message);
            StatusText = $"{name} の出力エラー: {ex.Message}";
        }

        OpenFolder(folder);
    }

    /// <summary>選択した帳票をまとめて出力する。1つ失敗しても他の出力は続ける(仕様書 第19章)。</summary>
    public void ExportSelectedReports() => ExportReports(Export.SelectedReports.ToList());

    /// <summary>
    /// 出席記録レポートだけを出力する(「2. 突合・確認・修正」の画面から直接出すため)。
    /// 出力されるのは画面に表示されている内容(編集後の値)そのままで、帳票出力タブから
    /// 出したものと同じです。
    /// </summary>
    public void ExportAttendanceReport()
    {
        var choice = AttendanceReportChoice;
        if (choice == null)
        {
            StatusText = "出席記録レポートは提供範囲外の設定になっています。";
            return;
        }
        ExportReports(new List<ReportChoice> { choice });
    }

    /// <summary>出席記録レポートの出力設定(テンプレートの指定を画面から使うため)。</summary>
    public ReportChoice? AttendanceReportChoice
        => Export.Reports.FirstOrDefault(r => r.Name == ReportExportViewModel.AttendanceReport);

    private void ExportReports(IReadOnlyList<ReportChoice> targets)
    {
        if (_result == null || _reportSheet == null)
        {
            StatusText = "先に突合を実行してください。";
            return;
        }

        if (targets.Count == 0)
        {
            StatusText = "出力する帳票を選んでください。";
            return;
        }

        if (CreateOutputFolder() is not { } folder) return;

        var period = $"{_result.TargetYear}年{_result.TargetMonth}月";
        int ok = 0, ng = 0;

        foreach (var choice in targets)
        {
            var path = Path.Combine(folder, choice.FileNameFor(_result.TargetYear, _result.TargetMonth));
            try
            {
                var written = RunExport(choice, path);
                foreach (var m in written.Messages) Messages.Add($"[{choice.Name}] {m}");

                Export.AddHistory(choice.Name, period, path, written.Success,
                                  written.Success ? $"{written.WrittenEmployees} 名 / {written.WrittenCells} セル" : "出力できませんでした");
                if (written.Success) ok++; else ng++;
            }
            catch (Exception ex)
            {
                // 1帳票の失敗で全体を止めない
                Messages.Add($"[{choice.Name}] 出力に失敗しました: {ex.Message}");
                Export.AddHistory(choice.Name, period, path, false, ex.Message);
                ng++;
            }
        }

        // 実行ログ(仕様書 第18.2章)
        try
        {
            var inputs = new List<LogInputFile>
            {
                LogInputFile.From("シフト表", ShiftPath, SelectedShiftSheet ?? ""),
                LogInputFile.From("打刻データ", PunchPath, SelectedPunchSheet ?? "")
            };
            if (TemplatePath.Length > 0) inputs.Add(LogInputFile.From("帳票テンプレート", TemplatePath));

            var logPath = ExecutionLog.Write(Path.Combine(folder, "ログ"), _result, inputs,
                                             Export.History.Where(h => h.Success).Select(h => h.Path).ToList(),
                                             _reportSheet.History.Entries);
            Messages.Add($"[実行ログ] {logPath}");
        }
        catch (Exception ex)
        {
            Messages.Add($"[実行ログ] 出力に失敗しました: {ex.Message}");
        }

        StatusText = ng == 0
            ? $"帳票を出力しました({ok} 件): {folder}"
            : $"帳票を出力しました(成功 {ok} 件 / 失敗 {ng} 件): {folder}";

        OpenFolder(folder);
    }

    /// <summary>
    /// 出力先のフォルダを作る(仕様書 OUT-005)。出力のたびに「年月_日時」のサブフォルダを作る。
    /// 出力先が決まっていない場合は null を返す。
    /// </summary>
    private string? CreateOutputFolder()
    {
        if (_result == null) return null;
        if (Export.OutputDirectory.Length == 0)
        {
            StatusText = "出力先フォルダが決まっていません。シフト表を指定してください。";
            return null;
        }

        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var folder = Path.Combine(Export.OutputDirectory, $"{_result.TargetYear}{_result.TargetMonth:00}_{stamp}");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private ReportOutputResult RunExport(ReportChoice choice, string path)
    {
        var result = _result!;
        var sheet = _reportSheet!;

        switch (choice.Name)
        {
            case ReportExportViewModel.AttendanceReport:
            {
                var rep = new AttendanceReportWriter().Write(sheet, choice.TemplatePath, path);
                var output = new ReportOutputResult
                {
                    ReportName = choice.Name,
                    Path = path,
                    Success = rep.Success,
                    WrittenEmployees = rep.TotalEmployees,
                    WrittenCells = rep.WrittenShiftCells + rep.WrittenPunchCells
                };
                output.Messages.AddRange(rep.Messages);
                return output;
            }

            case ReportExportViewModel.ApplicationForms:
            {
                var master = result.Masters?.ApplicationForms;
                var rows = master == null
                    ? new List<ApplicationFormRow>()
                    : ApplicationFormReport.Build(result, master);

                var output = new ReportOutputResult { ReportName = choice.Name, Path = path };
                if (rows.Count == 0)
                {
                    output.Messages.Add("申請書の用意が必要な日はありませんでした。");
                    output.Success = true;
                    return output;
                }
                ApplicationFormReport.Write(path, rows, result.TargetYear, result.TargetMonth);
                output.WrittenEmployees = rows.Select(r => r.PersonName).Distinct().Count();
                output.WrittenCells = rows.Count;
                output.Success = true;
                return output;
            }

            case ReportExportViewModel.AttendanceBook:
                return AttendanceBookWriter.Write(sheet, choice.TemplatePath, path);

            case ReportExportViewModel.PartTimePayroll:
                return PartTimePayrollWriter.Write(result, choice.TemplatePath, path,
                                                   result.Masters?.BreakRules ?? new Core.Masters.BreakRuleMaster());

            case ReportExportViewModel.DailySummary:
                return StandardReports.WriteDailySummary(result, path);

            case ReportExportViewModel.EditHistoryList:
                return StandardReports.WriteEditHistory(sheet, path);

            case ReportExportViewModel.PunchDetail:
                return StandardReports.WritePunchDetail(result, path);

            default:
                return new ReportOutputResult { ReportName = choice.Name, Path = path };
        }
    }

    private static void OpenFolder(string folder)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception)
        {
            // フォルダを開けなくても出力そのものは終わっているため、失敗として扱わない
        }
    }

    /// <summary>帳票出力タブのプレビューと部門一覧を更新する。</summary>
    public void RefreshExportPreview()
    {
        Export.UpdateDepartments(_result);
        Export.UpdatePreview(_result, _reportSheet);
        Forms.Update(_result, _reportSheet);
    }

    // ================= 絞り込みとページング(仕様書 v3.0 第8.2章) =================

    /// <summary>一覧の絞り込み方。</summary>
    public enum ReportFilterMode
    {
        /// <summary>全てのシフトを表示</summary>
        All,
        /// <summary>打刻データなしのみ表示(採用打刻が0件の日を含む社員)</summary>
        NoPunch,
        /// <summary>色付きセルのみ表示(異常判定または対象外を含む社員)</summary>
        Colored
    }

    private ReportFilterMode _reportFilter = ReportFilterMode.All;
    public ReportFilterMode ReportFilter
    {
        get => _reportFilter;
        set
        {
            if (!Set(ref _reportFilter, value)) return;
            CurrentPage = 1;
            RefreshReportView();
        }
    }

    private int _pageSize = 30;
    /// <summary>1ページに出す社員数。仕様書 8.2「表示件数を切替」。</summary>
    public int PageSize
    {
        get => _pageSize;
        set
        {
            if (!Set(ref _pageSize, Math.Max(1, value))) return;
            CurrentPage = 1;
            RefreshReportView();
        }
    }

    public IReadOnlyList<int> PageSizeChoices { get; } = new[] { 10, 20, 30, 50, 100 };

    private int _currentPage = 1;
    public int CurrentPage
    {
        get => _currentPage;
        set
        {
            if (!Set(ref _currentPage, Math.Clamp(value, 1, Math.Max(1, PageCount)))) return;
            RefreshReportView();
            OnPropertyChanged(nameof(PageText));
        }
    }

    /// <summary>絞り込み後の社員数(ページ分けの前)。</summary>
    private int _filteredCount;
    public int FilteredCount => _filteredCount;

    public int PageCount => Math.Max(1, (int)Math.Ceiling(_filteredCount / (double)PageSize));
    public string PageText => $"{CurrentPage} / {PageCount}";
    public string DisplayCountText => $"表示件数：{_filteredCount} 件";

    public void MoveFirst() => CurrentPage = 1;
    public void MovePrevious() => CurrentPage = CurrentPage - 1;
    public void MoveNext() => CurrentPage = CurrentPage + 1;
    public void MoveLast() => CurrentPage = PageCount;

    /// <summary>絞り込みとページ分けをやり直し、一覧に反映する。</summary>
    private void RefreshReportView()
    {
        var matched = ReportRows.Where(MatchesReportFilter).ToList();
        _filteredCount = matched.Count;

        if (CurrentPage > PageCount) _currentPage = PageCount;

        ReportRowsView.Clear();
        foreach (var row in matched.Skip((CurrentPage - 1) * PageSize).Take(PageSize))
            ReportRowsView.Add(row);

        foreach (var n in new[] { nameof(FilteredCount), nameof(PageCount),
                                  nameof(PageText), nameof(DisplayCountText) })
            OnPropertyChanged(n);
    }

    /// <summary>絞り込みの条件に合う社員か。</summary>
    private bool MatchesReportFilter(ReportRow row)
    {
        // 氏名・部門・作業番号の部分一致(仕様書 8.2「検索」)
        var text = (ReportSearchText ?? "").Trim();
        if (text.Length > 0 &&
            !row.Name.Contains(text) && !row.Department.Contains(text) && !row.EmployeeNo.Contains(text))
            return false;

        return ReportFilter switch
        {
            ReportFilterMode.NoPunch => row.HasNoPunchDay,
            ReportFilterMode.Colored => row.HasColoredDay,
            _ => true
        };
    }

    public RelayCommand ExportReportCommand { get; }
    public RelayCommand ExportApplicationFormListCommand { get; }
    public RelayCommand ExportUnresolvedCommand { get; }
    public RelayCommand OpenMastersCommand { get; }
    public RelayCommand ResetReportEditsCommand { get; }
    public RelayCommand SaveDraftCommand { get; }
    public RelayCommand OpenDraftCommand { get; }

    private void BrowseShift()
    {
        var dlg = new OpenFileDialog { Title = "シフト表を選択", Filter = "Excel ブック|*.xls;*.xlsx;*.xlsm|すべてのファイル|*.*" };
        if (dlg.ShowDialog() == true) ShiftPath = dlg.FileName;
    }

    private void BrowsePunch()
    {
        var dlg = new OpenFileDialog { Title = "タイムレコーダーデータを選択", Filter = "Excel ブック|*.xls;*.xlsx;*.xlsm|すべてのファイル|*.*" };
        if (dlg.ShowDialog() == true) PunchPath = dlg.FileName;
    }

    private void BrowseTemplate()
    {
        var dlg = new OpenFileDialog { Title = "出席記録レポートのテンプレートを選択", Filter = "Excel ブック|*.xlsx;*.xlsm|すべてのファイル|*.*" };
        if (dlg.ShowDialog() == true) TemplatePath = dlg.FileName;
    }

    private void LoadShiftSheets()
    {
        ShiftSheets.Clear();
        if (!File.Exists(ShiftPath)) return;
        try
        {
            foreach (var n in ExcelHelper.SheetNames(ShiftPath)) ShiftSheets.Add(n);
            // 「修正」を含むシート(最終確定版)を優先して選ぶ
            SelectedShiftSheet = ShiftSheets.LastOrDefault(n => n.Contains("修正")) ?? ShiftSheets.FirstOrDefault();
        }
        catch (Exception ex) { StatusText = $"シフト表を開けません: {ex.Message}"; }
    }

    private void LoadPunchSheets()
    {
        PunchSheets.Clear();
        if (!File.Exists(PunchPath)) return;
        try
        {
            foreach (var n in ExcelHelper.SheetNames(PunchPath)) PunchSheets.Add(n);
            SelectedPunchSheet = PunchSheets.FirstOrDefault(n => n.Contains(PunchParser.DefaultSheetKeyword))
                                 ?? PunchSheets.FirstOrDefault();
        }
        catch (Exception ex) { StatusText = $"打刻データを開けません: {ex.Message}"; }
    }

    private void Run() => Run(null, null);

    /// <param name="draft">
    /// 保留を開いたときの帳票。指定すると突合し直した結果にその編集を重ねる。
    /// 保留の内容が失われることはないため、破棄の確認も出さない。
    /// </param>
    /// <param name="draftPath">重ねた保留ファイルの場所。</param>
    private void Run(ReportSheet? draft, string? draftPath)
    {
        if (draft == null && !ConfirmDiscardEdits("突合を実行すると")) return;

        IsBusy = true;
        StatusText = "突合しています...";
        try
        {
            LoadShiftTypes();

            var request = new MatchingRequest
            {
                ShiftPath = ShiftPath,
                ShiftSheetName = SelectedShiftSheet,
                PunchPath = PunchPath,
                PunchSheetName = SelectedPunchSheet,
                TargetYearMonth = AutoDetectYearMonth ? null : (TargetYear, TargetMonth),
                MastersDirectory = MasterSet.DefaultDirectory,
                Options = new MatchingOptions { OnlyPersonsInShift = OnlyPersonsInShift }
            };

            var result = _service.Execute(request);
            _result = result;

            Details.Clear();
            foreach (var d in result.Details) Details.Add(d);
            UnresolvedNames.Clear();
            foreach (var u in result.UnresolvedNames) UnresolvedNames.Add(u);
            Messages.Clear();
            foreach (var m in result.Messages) Messages.Add(m);

            if (result.TargetYear > 0)
            {
                TargetYear = result.TargetYear;
                TargetMonth = result.TargetMonth;
            }

            SummaryText =
                $"{result.TargetYear}年{result.TargetMonth}月 / 対象 {result.PersonCount} 名 / 明細 {result.Details.Count} 件 " +
                $"(シフト {result.ShiftRecordCount} 件・打刻 {result.PunchRecordCount} 件を読込)";
            StatusText = $"完了 ({result.Elapsed.TotalMilliseconds:0} ms) execution_id = {result.ExecutionId}";

            foreach (var n in new[] { nameof(PersonCount), nameof(NormalCount), nameof(LateCount),
                                      nameof(EarlyLeaveCount), nameof(EarlyInCount), nameof(OvertimeCount),
                                      nameof(ReviewCount), nameof(ExcludedCount), nameof(DetailCount) })
                OnPropertyChanged(n);

            DetailsView.Refresh();

            // 帳票と同じレイアウトの編集用モデルを組み立てる
            BuildReportSheet(draft, draftPath);
        }
        catch (Exception ex)
        {
            StatusText = $"エラー: {ex.Message}";
            Messages.Add($"[例外] {ex}");
        }
        finally { IsBusy = false; }
    }

    private void ExportReport()
    {
        if (_reportSheet == null) return;
        if (!File.Exists(TemplatePath))
        {
            StatusText = "先に出席記録レポートのテンプレートを選択してください。";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "出席記録レポートの保存先",
            Filter = "Excel ブック|*.xlsx",
            FileName = $"出席記録レポート_{_reportSheet.Year}{_reportSheet.Month:00}.xlsx"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            // 画面(出席記録レポートタブ)の内容をそのまま書き出す
            var writer = new AttendanceReportWriter();
            var rep = writer.Write(_reportSheet, TemplatePath, dlg.FileName);
            foreach (var m in rep.Messages) Messages.Add("[帳票] " + m);

            if (rep.Success)
            {
                Messages.Add($"[帳票] {rep.TotalEmployees} 名を出力(値のある社員 {rep.WrittenEmployees} 名 / " +
                             $"シフト {rep.WrittenShiftCells} セル・打刻 {rep.WrittenPunchCells} セル" +
                             (rep.WrittenStatuses > 0 ? $" / 状態の記入 {rep.WrittenStatuses} 日" : "") +
                             (rep.EditedCells > 0 ? $" / 画面で編集 {rep.EditedCells} セル" : "") + ")");
                if (rep.AddedEmployees.Count > 0)
                    Messages.Add($"[帳票] テンプレートに無いため末尾に追加した社員: {string.Join(", ", rep.AddedEmployees)}");
                StatusText = $"出席記録レポートを出力しました: {dlg.FileName}";

                AskToOpen(dlg.FileName, rep);
            }
            else StatusText = "出席記録レポートの出力に失敗しました。処理ログを確認してください。";
        }
        catch (Exception ex)
        {
            StatusText = $"帳票出力エラー: {ex.Message}";
            Messages.Add($"[例外] {ex}");
        }
    }

    /// <summary>
    /// 申請書 確認一覧を出力する(勤怠締め業務フロー STEP1 ④「申請書を印刷」)。
    ///
    /// 突合の判定から、その日に用意が必要な申請書を申請書マスタで引き当てて一覧にする。
    /// 帳票の編集内容ではなく突合結果を対象にするため、突合実行の直後から出力できる。
    /// </summary>
    private void ExportApplicationFormList()
    {
        if (_result?.Masters?.ApplicationForms is not { } master) return;

        var rows = ApplicationFormReport.Build(_result, master);
        if (rows.Count == 0)
        {
            StatusText = "申請書の用意が必要な日はありませんでした。";
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "申請書 確認一覧の保存先",
            Filter = "Excel ブック|*.xlsx",
            FileName = $"申請書確認一覧_{_result.TargetYear}{_result.TargetMonth:00}.xlsx"
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            ApplicationFormReport.Write(dlg.FileName, rows, _result.TargetYear, _result.TargetMonth);

            foreach (var g in rows.GroupBy(r => r.FormName).OrderByDescending(g => g.Count()))
                Messages.Add($"[申請書] {g.Key} : {g.Count()} 件 ({g.Select(r => r.PersonName).Distinct().Count()} 名)");

            StatusText = $"申請書 確認一覧を出力しました({rows.Count} 件): {dlg.FileName}";
            AskToOpenFile(dlg.FileName,
                $"申請書 確認一覧を出力しました。\n\n{dlg.FileName}\n\n" +
                $"{rows.Select(r => r.FormName).Distinct().Count()} 種類 / {rows.Count} 件\n\nExcel で開きますか?",
                "申請書 確認一覧の出力");
        }
        catch (Exception ex)
        {
            StatusText = $"申請書 確認一覧の出力エラー: {ex.Message}";
            Messages.Add($"[例外] {ex}");
        }
    }

    private void ExportUnresolved()
    {
        var dlg = new SaveFileDialog
        {
            Title = "未解決氏名の書き出し(別名マスタの下書き)",
            Filter = "XML ファイル|*.xml",
            FileName = "name_alias_未解決.xml"
        };
        if (dlg.ShowDialog() != true) return;

        File.WriteAllText(dlg.FileName, AliasMaster.BuildXmlTemplate(UnresolvedNames), new UTF8Encoding(false));
        StatusText = $"未解決氏名を書き出しました: {dlg.FileName}" +
                     $"(canonical に正式氏名を記入して masters/{MasterSet.AliasFileName} に追記してください)";
    }

    /// <summary>
    /// 出力した帳票をその場で開くか確認する。
    /// 出力後は中身を確認することがほとんどのため、確認だけで開けるようにしている。
    /// </summary>
    private void AskToOpen(string path, ReportWriteResult rep)
    {
        var summary = $"出席記録レポートを出力しました。\n\n{path}\n\n" +
                      $"社員 {rep.TotalEmployees} 名" +
                      (rep.WrittenStatuses > 0 ? $" / 遅刻・早退などの日 {rep.WrittenStatuses} 件は状態行に書き、黄色で塗っています" : "") +
                      "\n\nExcel で開きますか?";
        AskToOpenFile(path, summary, "出席記録レポートの出力");
    }

    /// <summary>出力したブックをそのまま Excel で開くか確認する。</summary>
    private void AskToOpenFile(string path, string summary, string caption)
    {
        var answer = System.Windows.MessageBox.Show(summary, caption,
                                                   System.Windows.MessageBoxButton.YesNo,
                                                   System.Windows.MessageBoxImage.Information);
        if (answer != System.Windows.MessageBoxResult.Yes) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true      // 関連付けられたアプリ(Excel)で開く
            });
        }
        catch (Exception ex)
        {
            StatusText = $"ファイルを開けません: {ex.Message}";
            Messages.Add($"[帳票] 出力したファイルを開けませんでした: {ex.Message}");
        }
    }

    private void OpenMastersFolder()
    {
        var dir = MasterSet.DefaultDirectory;
        Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = dir,
            UseShellExecute = true
        });
    }
}
