using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Naming;

namespace TakaneAttendance.Wpf.ViewModels;

/// <summary>勤務区分の種別の選択肢。マスタ編集画面のコンボボックスに出す。</summary>
public sealed record ShiftKindChoice(ShiftKind Value, string Label)
{
    /// <summary>Unknown は「マスタ未登録」を表す内部の値のため、選択肢には出さない。</summary>
    public static IReadOnlyList<ShiftKindChoice> All { get; } = new[]
    {
        new ShiftKindChoice(ShiftKind.Work,         "通常勤務(開始時刻あり)"),
        new ShiftKindChoice(ShiftKind.DayOff,       "公休"),
        new ShiftKindChoice(ShiftKind.PaidLeave,    "有給"),
        new ShiftKindChoice(ShiftKind.BusinessTrip, "出張(終日・半日は打刻件数で判定)"),
        new ShiftKindChoice(ShiftKind.Other,        "その他(欠勤・特別休暇など)"),
        new ShiftKindChoice(ShiftKind.Excluded,     "対象外(休場日など)"),
    };
}

/// <summary>別名マスタの1行。</summary>
public sealed class AliasRow : ObservableObject
{
    private string _source = "";
    /// <summary>シフト表などに書かれている表記(例「平山 部長」)</summary>
    public string Source { get => _source; set => Set(ref _source, value); }

    private string _canonical = "";
    /// <summary>打刻データに登録されている正式氏名。空欄なら「正式氏名そのものの登録」。</summary>
    public string Canonical { get => _canonical; set => Set(ref _canonical, value); }

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    public AliasRow() { }

    public AliasRow(AliasEntry entry)
    {
        _source = entry.Source;
        _canonical = entry.Canonical;
        _note = entry.Note;
    }

    public AliasEntry ToEntry() => new(Source.Trim(), Canonical.Trim(), Note.Trim());
}

/// <summary>勤務区分マスタの1行。</summary>
public sealed class ShiftTypeRow : ObservableObject
{
    private string _code = "";
    /// <summary>シフト表のセルに入る文字値(公・有・欠 など)</summary>
    public string Code { get => _code; set => Set(ref _code, value); }

    private ShiftKind _kind = ShiftKind.Work;
    public ShiftKind Kind { get => _kind; set => Set(ref _kind, value); }

    private string _description = "";
    public string Description { get => _description; set => Set(ref _description, value); }

    public ShiftTypeRow() { }

    public ShiftTypeRow(ShiftTypeEntry entry)
    {
        _code = entry.Code;
        _kind = entry.Kind;
        _description = entry.Description;
    }

    public ShiftTypeEntry ToEntry() => new(Code.Trim(), Kind, Description.Trim());
}
/// <summary>所定労働時間マスタの所属部1行。</summary>
public sealed class WorkingHoursDivisionRow : ObservableObject
{
    private string _name = "";
    /// <summary>所属部(業務部 / 総務部 / 食堂部 / コース管理部 など)。従業員マスタの所属部と同じ文字。</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _weekdayBreak = "90";
    /// <summary>平日の休憩(分)。所定労働時間に足して予定終了を求める。</summary>
    public string WeekdayBreak { get => _weekdayBreak; set => Set(ref _weekdayBreak, value); }

    private string _holidayBreak = "90";
    /// <summary>土日祝の休憩(分)</summary>
    public string HolidayBreak { get => _holidayBreak; set => Set(ref _holidayBreak, value); }

    public WorkingHoursDivisionRow() { }

    public WorkingHoursDivisionRow(WorkingHoursDivisionEntry entry)
    {
        _name = entry.Name;
        _weekdayBreak = entry.WeekdayBreak.Length > 0 ? entry.WeekdayBreak : "90";
        _holidayBreak = entry.HolidayBreak.Length > 0 ? entry.HolidayBreak : _weekdayBreak;
    }

    public WorkingHoursDivisionEntry ToEntry() => new(Name.Trim(), WeekdayBreak.Trim(), HolidayBreak.Trim());
}

/// <summary>所定労働時間マスタの期間1行。</summary>
public sealed class WorkingHoursPeriodRow : ObservableObject
{
    private string _division = "";
    /// <summary>どの所属部の期間か(上の一覧の所属部名)</summary>
    public string Division { get => _division; set => Set(ref _division, value); }

    private string _from = "";
    /// <summary>開始(月/日)</summary>
    public string From { get => _from; set => Set(ref _from, value); }

    private string _to = "";
    /// <summary>終了(月/日)。開始より前なら年をまたぐ区間。</summary>
    public string To { get => _to; set => Set(ref _to, value); }

    private string _weekday = "";
    /// <summary>平日の所定労働時間(時間)</summary>
    public string Weekday { get => _weekday; set => Set(ref _weekday, value); }

    private string _holiday = "";
    /// <summary>土日祝の所定労働時間(時間)。空欄なら平日と同じ。</summary>
    public string Holiday { get => _holiday; set => Set(ref _holiday, value); }

    private string _weekdayBreak = "";
    /// <summary>この期間の平日の休憩(分)。空欄なら所属部の既定を使う。</summary>
    public string WeekdayBreak { get => _weekdayBreak; set => Set(ref _weekdayBreak, value); }

    private string _holidayBreak = "";
    /// <summary>この期間の土日祝の休憩(分)。空欄なら所属部の既定を使う。</summary>
    public string HolidayBreak { get => _holidayBreak; set => Set(ref _holidayBreak, value); }

    public WorkingHoursPeriodRow() { }

    public WorkingHoursPeriodRow(WorkingHoursPeriodEntry entry)
    {
        _division = entry.Division;
        _from = entry.From;
        _to = entry.To;
        _weekday = entry.Weekday;
        _holiday = entry.Holiday;
        _weekdayBreak = entry.WeekdayBreak;
        _holidayBreak = entry.HolidayBreak;
    }

    public WorkingHoursPeriodEntry ToEntry() => new(Division.Trim(), From.Trim(), To.Trim(),
                                                    Weekday.Trim(), Holiday.Trim(),
                                                    WeekdayBreak.Trim(), HolidayBreak.Trim());
}
/// <summary>従業員マスタの雇用区分の選択肢(正社員を含む)。</summary>
public sealed record StaffEmploymentChoice(string Value, string Label)
{
    public static IReadOnlyList<StaffEmploymentChoice> All { get; } = new[]
    {
        new StaffEmploymentChoice("正社員", "正社員"),
        new StaffEmploymentChoice("パート", "パート"),
        new StaffEmploymentChoice("アルバイト", "アルバイト"),
    };
}

/// <summary>祝日マスタの区分の選択肢。</summary>
public sealed record HolidayKindChoice(string Value, string Label)
{
    public static IReadOnlyList<HolidayKindChoice> All { get; } = new[]
    {
        new HolidayKindChoice("祝",   "祝日(土日と同じ扱い)"),
        new HolidayKindChoice("休場", "休場日(対象外・グレー表示)"),
    };
}

/// <summary>打刻の丸め方の選択肢。</summary>
public sealed record RoundingChoice(string Value, string Label)
{
    public static IReadOnlyList<RoundingChoice> All { get; } = new[]
    {
        new RoundingChoice("up",      "切り上げ"),
        new RoundingChoice("down",    "切り捨て"),
        new RoundingChoice("nearest", "四捨五入"),
    };
}

/// <summary>申請書の選択肢。</summary>
public sealed record FormNameChoice(string Value)
{
    public static IReadOnlyList<FormNameChoice> All { get; } = new[]
    {
        new FormNameChoice("タイムカード修正届出書"),
        new FormNameChoice("年次有休休暇・欠勤申請書"),
        new FormNameChoice("出張届"),
        new FormNameChoice("勤怠管理簿"),
    };
}

/// <summary>従業員マスタの1行。</summary>
public sealed class EmployeeRow : ObservableObject
{
    private bool _managed = true;
    /// <summary>
    /// 管理区分。チェックを付けた方だけを突合結果の一覧と帳票の対象にする。
    /// 新しく足した行は対象(チェック済み)から始める。
    /// </summary>
    public bool Managed { get => _managed; set => Set(ref _managed, value); }

    private string _no = "";
    /// <summary>社員番号(表示用。突合キーには使わない)</summary>
    public string No { get => _no; set => Set(ref _no, value); }

    private string _name = "";
    /// <summary>正式氏名(必須)。突合キーになる。</summary>
    public string Name { get => _name; set => Set(ref _name, value); }

    private string _division = "";
    /// <summary>所属部(必須)。所定労働時間マスタの引き当てキー。</summary>
    public string Division { get => _division; set => Set(ref _division, value); }

    private string _department = "";
    /// <summary>所属課</summary>
    public string Department { get => _department; set => Set(ref _department, value); }

    private string _employment = "正社員";
    public string Employment { get => _employment; set => Set(ref _employment, value); }

    private string _pattern = "";
    /// <summary>部門(ハウス・コース など勤務地)</summary>
    public string Pattern { get => _pattern; set => Set(ref _pattern, value); }

    private string _workHours = "";
    /// <summary>1日の拘束時間(休憩込み)。パート・アルバイトの予定終了 = 予定開始 + この時間。</summary>
    public string WorkHours { get => _workHours; set => Set(ref _workHours, value); }

    private string _hourlyWage = "";
    /// <summary>基本時給(円)。給与計算表の時給欄。</summary>
    public string HourlyWage { get => _hourlyWage; set => Set(ref _hourlyWage, value); }

    private string _joined = "";
    public string Joined { get => _joined; set => Set(ref _joined, value); }

    private string _left = "";
    public string Left { get => _left; set => Set(ref _left, value); }

    public EmployeeRow() { }

    public EmployeeRow(EmployeeEditEntry entry)
    {
        _managed = entry.Managed;
        _no = entry.No;
        _name = entry.Name;
        _division = entry.Division;
        _department = entry.Department;
        _employment = entry.Employment.Length > 0 ? entry.Employment : "正社員";
        _pattern = entry.Pattern;
        _workHours = entry.WorkHours;
        _hourlyWage = entry.HourlyWage;
        _joined = entry.Joined;
        _left = entry.Left;
    }

    public EmployeeEditEntry ToEntry() => new(No.Trim(), Name.Trim(), Division.Trim(), Department.Trim(),
                                              Employment.Trim(), Pattern.Trim(), WorkHours.Trim(),
                                              HourlyWage.Trim(), Joined.Trim(), Left.Trim(), Managed);
}

/// <summary>祝日マスタの1件。</summary>
public sealed class HolidayRow : ObservableObject
{
    private string _date = "";
    /// <summary>月/日(例 7/20)。毎年同じ日で運用するため年は持たない。</summary>
    public string Date { get => _date; set => Set(ref _date, value); }

    private string _kind = "祝";
    public string Kind { get => _kind; set => Set(ref _kind, value); }

    private string _note = "";
    public string Note { get => _note; set => Set(ref _note, value); }

    public HolidayRow() { }

    public HolidayRow(HolidayEntry entry)
    {
        _date = entry.Date;
        _kind = entry.Kind.Length > 0 ? entry.Kind : "祝";
        _note = entry.Note;
    }

    public HolidayEntry ToEntry() => new(Date.Trim(), Kind.Trim(), Note.Trim());
}

/// <summary>申請書マスタの1行。</summary>
public sealed class ApplicationFormMappingRow : ObservableObject
{
    private string _code = "";
    /// <summary>判定コード(NO_PUNCH / LATE など)</summary>
    public string Code { get => _code; set => Set(ref _code, value); }

    private string _formName = "";
    public string FormName { get => _formName; set => Set(ref _formName, value); }

    private string _reason = "";
    /// <summary>申請書の「理由」欄に出る文言</summary>
    public string Reason { get => _reason; set => Set(ref _reason, value); }

    public ApplicationFormMappingRow() { }

    public ApplicationFormMappingRow(ApplicationFormEntry entry)
    {
        _code = entry.Code;
        _formName = entry.FormName;
        _reason = entry.Reason;
    }

    public ApplicationFormEntry ToEntry() => new(Code.Trim(), FormName.Trim(), Reason.Trim());
}
/// <summary>休憩ルールの段階1行。</summary>
public sealed class BreakBandRow : ObservableObject
{
    private string _upToHours = "";
    /// <summary>拘束時間の上限(時間)。空欄なら「それ以上すべて」。</summary>
    public string UpToHours { get => _upToHours; set => Set(ref _upToHours, value); }

    private string _breakMinutes = "";
    public string BreakMinutes { get => _breakMinutes; set => Set(ref _breakMinutes, value); }

    public BreakBandRow() { }

    public BreakBandRow(BreakBandEntry entry)
    {
        _upToHours = entry.UpToHours;
        _breakMinutes = entry.BreakMinutes;
    }

    public BreakBandEntry ToEntry() => new(UpToHours.Trim(), BreakMinutes.Trim());
}

/// <summary>
/// マスタ編集画面のビューモデル。
///
/// masters フォルダの8つのXML(従業員・別名・勤務区分・所定労働時間・祝日・申請書・判定閾値・休憩ルール)を
/// 画面で修正できるようにする。
/// 保存は8ファイルまとめて行い、書式の誤りがある場合は1件も書き込まない。
/// </summary>
public sealed class MasterEditorViewModel : ObservableObject
{
    private readonly List<UnresolvedName> _unresolved;
    private bool _loading;

    public MasterEditorViewModel(string directory, IEnumerable<UnresolvedName>? unresolved = null)
    {
        Directory = directory;
        _unresolved = unresolved?.ToList() ?? new List<UnresolvedName>();

        SaveCommand = new RelayCommand(Save);
        ReloadCommand = new RelayCommand(Reload);

        AddAliasCommand = new RelayCommand(() => SelectedAlias = AddRow(Aliases, new AliasRow()));
        RemoveAliasCommand = new RelayCommand(() => Remove(Aliases, SelectedAlias), () => SelectedAlias != null);
        ImportUnresolvedCommand = new RelayCommand(ImportUnresolved, () => _unresolved.Count > 0);

        AddShiftTypeCommand = new RelayCommand(() => SelectedShiftType = AddRow(ShiftTypes, new ShiftTypeRow()));
        RemoveShiftTypeCommand = new RelayCommand(() => Remove(ShiftTypes, SelectedShiftType), () => SelectedShiftType != null);

        AddWorkingHoursDivisionCommand = new RelayCommand(
            () => SelectedWorkingHoursDivision = AddRow(WorkingHoursDivisions, new WorkingHoursDivisionRow()));
        RemoveWorkingHoursDivisionCommand = new RelayCommand(
            () => Remove(WorkingHoursDivisions, SelectedWorkingHoursDivision), () => SelectedWorkingHoursDivision != null);

        AddWorkingHoursPeriodCommand = new RelayCommand(
            () => SelectedWorkingHoursPeriod = AddRow(WorkingHoursPeriods,
                new WorkingHoursPeriodRow { Division = SelectedWorkingHoursDivision?.Name ?? WorkingHoursDivisions.FirstOrDefault()?.Name ?? "" }));
        RemoveWorkingHoursPeriodCommand = new RelayCommand(
            () => Remove(WorkingHoursPeriods, SelectedWorkingHoursPeriod), () => SelectedWorkingHoursPeriod != null);

        AddEmployeeCommand = new RelayCommand(() => SelectedEmployee = AddRow(Employees, new EmployeeRow()));
        RemoveEmployeeCommand = new RelayCommand(() => Remove(Employees, SelectedEmployee), () => SelectedEmployee != null);
        UncheckAllEmployeesCommand = new RelayCommand(UncheckAllEmployees, () => Employees.Any(e => e.Managed));

        AddHolidayCommand = new RelayCommand(() => SelectedHoliday = AddRow(Holidays, new HolidayRow()));
        RemoveHolidayCommand = new RelayCommand(() => Remove(Holidays, SelectedHoliday), () => SelectedHoliday != null);

        AddApplicationFormCommand = new RelayCommand(() => SelectedApplicationForm = AddRow(ApplicationForms, new ApplicationFormMappingRow()));
        RemoveApplicationFormCommand = new RelayCommand(() => Remove(ApplicationForms, SelectedApplicationForm), () => SelectedApplicationForm != null);

        AddBreakBandCommand = new RelayCommand(() => SelectedBreakBand = AddRow(BreakBands, new BreakBandRow()));
        RemoveBreakBandCommand = new RelayCommand(() => Remove(BreakBands, SelectedBreakBand), () => SelectedBreakBand != null);

        // 従業員タブは「管理区分」にチェックが付いた方だけを出す(全件表示に切り替えられる)
        EmployeesView = System.Windows.Data.CollectionViewSource.GetDefaultView(Employees);
        EmployeesView.Filter = o => ShowAllEmployees || o is not EmployeeRow row || row.Managed;
        Employees.CollectionChanged += (_, _) => NotifyEmployeeCounts();

        Watch(Aliases);
        Watch(ShiftTypes);
        Watch(WorkingHoursDivisions);
        Watch(WorkingHoursPeriods);
        Watch(Employees);
        Watch(Holidays);
        Watch(ApplicationForms);
        Watch(BreakBands);

        Load();
    }

    /// <summary>マスタXMLの置き場所(実行ファイル配下の masters)。</summary>
    public string Directory { get; }

    /// <summary>一度でも保存したか。呼び出し元がマスタを読み直すかの判断に使う。</summary>
    public bool Saved { get; private set; }

    /// <summary>画面で変更したが、まだ保存していない内容があるか。</summary>
    private bool _isDirty;
    public bool IsDirty { get => _isDirty; private set => Set(ref _isDirty, value); }

    public ObservableCollection<AliasRow> Aliases { get; } = new();
    public ObservableCollection<ShiftTypeRow> ShiftTypes { get; } = new();
    public ObservableCollection<WorkingHoursDivisionRow> WorkingHoursDivisions { get; } = new();
    public ObservableCollection<WorkingHoursPeriodRow> WorkingHoursPeriods { get; } = new();
    public ObservableCollection<EmployeeRow> Employees { get; } = new();

    /// <summary>
    /// 従業員マスタの表示用の一覧。「管理区分」にチェックが付いた方だけを画面に出す。
    /// チェックを外した方を戻せるよう、<see cref="ShowAllEmployees"/> で全件表示に切り替えられる。
    /// 保存するのは <see cref="Employees"/> の全件で、表示の絞り込みは保存内容に影響しない。
    /// </summary>
    public System.ComponentModel.ICollectionView EmployeesView { get; }

    private bool _showAllEmployees;
    /// <summary>管理区分のチェックが外れている方も表示するか(チェックを付け直すために使う)。</summary>
    public bool ShowAllEmployees
    {
        get => _showAllEmployees;
        set { if (Set(ref _showAllEmployees, value)) RefreshEmployeesView(); }
    }

    /// <summary>
    /// 管理区分のチェックを全員ぶん外す。管理する方だけを選び直すときに使う。
    ///
    /// 全員が対象外になると一覧が空になり、チェックを付け直せなくなるため、
    /// 「対象外も表示」に切り替えてから外す。
    /// </summary>
    private void UncheckAllEmployees()
    {
        int cleared = Employees.Count(e => e.Managed);
        if (cleared == 0) return;

        ShowAllEmployees = true;
        foreach (var row in Employees) row.Managed = false;
        RefreshEmployeesView();

        StatusText = $"管理区分のチェックを全て外しました({cleared} 名を対象外にしました / 全 {Employees.Count} 名)。" +
                     "一覧は「対象外も表示」に切り替えたので、対象にする方にチェックを付けてください。" +
                     "「保存」を押すまでXMLには反映されません(「読み直す」で元に戻せます)。";
    }

    /// <summary>管理区分にチェックが付いている人数(画面と帳票の対象になる人数)。</summary>
    public int ManagedEmployeeCount => Employees.Count(e => e.Managed);

    /// <summary>従業員タブの見出しに出す、対象人数の案内。</summary>
    public string EmployeeCountText
        => ManagedEmployeeCount == Employees.Count
            ? $"管理対象 {ManagedEmployeeCount} 名(全 {Employees.Count} 名)"
            : $"管理対象 {ManagedEmployeeCount} 名 / 対象外 {Employees.Count - ManagedEmployeeCount} 名" +
              $"(全 {Employees.Count} 名)";

    /// <summary>
    /// 管理区分の絞り込みをやり直す。
    ///
    /// チェックの切り替えは DataGrid のセル編集中に起きるため、その場で Refresh すると
    /// 「編集中は並べ替えできません」で落ちる。入力の処理が終わってから並べ直す。
    /// </summary>
    private void RefreshEmployeesView()
    {
        NotifyEmployeeCounts();

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            TryRefreshEmployeesView();
            return;
        }

        dispatcher.BeginInvoke(new Action(TryRefreshEmployeesView),
                               System.Windows.Threading.DispatcherPriority.Background);
    }

    private void NotifyEmployeeCounts()
    {
        foreach (var n in new[] { nameof(ManagedEmployeeCount), nameof(EmployeeCountText) })
            OnPropertyChanged(n);
    }

    private void TryRefreshEmployeesView()
    {
        try
        {
            EmployeesView.Refresh();
        }
        catch (InvalidOperationException)
        {
            // まだ編集中の行がある場合。次にチェックを切り替えた時か、タブを開き直した時に反映される。
        }
    }

    public ObservableCollection<HolidayRow> Holidays { get; } = new();
    public ObservableCollection<ApplicationFormMappingRow> ApplicationForms { get; } = new();
    public ObservableCollection<BreakBandRow> BreakBands { get; } = new();

    private AliasRow? _selectedAlias;
    public AliasRow? SelectedAlias { get => _selectedAlias; set => Set(ref _selectedAlias, value); }

    private ShiftTypeRow? _selectedShiftType;
    public ShiftTypeRow? SelectedShiftType { get => _selectedShiftType; set => Set(ref _selectedShiftType, value); }

    private WorkingHoursDivisionRow? _selectedWorkingHoursDivision;
    public WorkingHoursDivisionRow? SelectedWorkingHoursDivision
    {
        get => _selectedWorkingHoursDivision;
        set => Set(ref _selectedWorkingHoursDivision, value);
    }

    private WorkingHoursPeriodRow? _selectedWorkingHoursPeriod;
    public WorkingHoursPeriodRow? SelectedWorkingHoursPeriod
    {
        get => _selectedWorkingHoursPeriod;
        set => Set(ref _selectedWorkingHoursPeriod, value);
    }

    private EmployeeRow? _selectedEmployee;
    public EmployeeRow? SelectedEmployee { get => _selectedEmployee; set => Set(ref _selectedEmployee, value); }

    private HolidayRow? _selectedCalendarDay;
    public HolidayRow? SelectedHoliday { get => _selectedCalendarDay; set => Set(ref _selectedCalendarDay, value); }

    private ApplicationFormMappingRow? _selectedApplicationForm;
    public ApplicationFormMappingRow? SelectedApplicationForm { get => _selectedApplicationForm; set => Set(ref _selectedApplicationForm, value); }

    private BreakBandRow? _selectedBreakBand;
    public BreakBandRow? SelectedBreakBand { get => _selectedBreakBand; set => Set(ref _selectedBreakBand, value); }

    // ---- 休憩ルール(丸めの設定) ----

    private string _breakUnitMinutes = "15";
    /// <summary>打刻の丸めの単位(分)</summary>
    public string BreakUnitMinutes { get => _breakUnitMinutes; set { if (Set(ref _breakUnitMinutes, value)) MarkDirty(); } }

    private string _breakInRounding = "up";
    /// <summary>出勤の丸め方(up / down / nearest)</summary>
    public string BreakInRounding { get => _breakInRounding; set { if (Set(ref _breakInRounding, value)) MarkDirty(); } }

    private string _breakOutRounding = "down";
    /// <summary>退勤の丸め方(up / down / nearest)</summary>
    public string BreakOutRounding { get => _breakOutRounding; set { if (Set(ref _breakOutRounding, value)) MarkDirty(); } }

    // ---- 判定閾値 ----

    private string _earlyInMinutes = "30";
    /// <summary>早出とみなす分(予定開始より何分早いか)</summary>
    public string EarlyInMinutes { get => _earlyInMinutes; set { if (Set(ref _earlyInMinutes, value)) MarkDirty(); } }

    private string _overtimeMinutes = "30";
    /// <summary>時間外とみなす分(予定終了より何分遅いか)</summary>
    public string OvertimeMinutes { get => _overtimeMinutes; set { if (Set(ref _overtimeMinutes, value)) MarkDirty(); } }

    private string _fullTimeSpanMinutes = "570";
    /// <summary>所定労働時間マスタに登録の無い所属部で使う拘束時間(分)</summary>
    public string FullTimeSpanMinutes { get => _fullTimeSpanMinutes; set { if (Set(ref _fullTimeSpanMinutes, value)) MarkDirty(); } }

    private string _toleranceMinutes = "0";
    /// <summary>遅刻・早退の許容(分)</summary>
    public string ToleranceMinutes { get => _toleranceMinutes; set { if (Set(ref _toleranceMinutes, value)) MarkDirty(); } }

    private string _statusText = "";
    public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

    private string _errorText = "";
    /// <summary>保存できない理由(空欄なら問題なし)。</summary>
    public string ErrorText
    {
        get => _errorText;
        set { if (Set(ref _errorText, value)) OnPropertyChanged(nameof(HasError)); }
    }

    public bool HasError => ErrorText.Length > 0;

    public RelayCommand SaveCommand { get; }
    public RelayCommand ReloadCommand { get; }
    public RelayCommand AddAliasCommand { get; }
    public RelayCommand RemoveAliasCommand { get; }
    public RelayCommand ImportUnresolvedCommand { get; }
    public RelayCommand AddShiftTypeCommand { get; }
    public RelayCommand RemoveShiftTypeCommand { get; }
    public RelayCommand AddWorkingHoursDivisionCommand { get; }
    public RelayCommand RemoveWorkingHoursDivisionCommand { get; }
    public RelayCommand AddWorkingHoursPeriodCommand { get; }
    public RelayCommand RemoveWorkingHoursPeriodCommand { get; }
    public RelayCommand AddEmployeeCommand { get; }
    public RelayCommand RemoveEmployeeCommand { get; }
    public RelayCommand UncheckAllEmployeesCommand { get; }
    public RelayCommand AddHolidayCommand { get; }
    public RelayCommand RemoveHolidayCommand { get; }
    public RelayCommand AddApplicationFormCommand { get; }
    public RelayCommand RemoveApplicationFormCommand { get; }
    public RelayCommand AddBreakBandCommand { get; }
    public RelayCommand RemoveBreakBandCommand { get; }

    // ================= 読み込み =================

    private string FilePath(string fileName) => System.IO.Path.Combine(Directory, fileName);

    private void Load()
    {
        _loading = true;
        try
        {
            Aliases.Clear();
            ShiftTypes.Clear();
            WorkingHoursDivisions.Clear();
            WorkingHoursPeriods.Clear();
            Employees.Clear();
            Holidays.Clear();
            ApplicationForms.Clear();
            BreakBands.Clear();

            var messages = new List<string>();

            foreach (var a in MasterEditor.LoadAliases(FilePath(MasterSet.AliasFileName), messages))
                Aliases.Add(new AliasRow(a));

            // 勤務区分はアプリの既定値(公・有・欠 …)と合わせた、実際に使われる一覧を出す
            foreach (var s in ShiftTypeMaster.Load(FilePath(MasterSet.ShiftTypeFileName)).All)
                ShiftTypes.Add(new ShiftTypeRow(s));

            var (divisions, periods) = MasterEditor.LoadWorkingHours(FilePath(MasterSet.WorkingHoursFileName), messages);
            foreach (var g in divisions) WorkingHoursDivisions.Add(new WorkingHoursDivisionRow(g));
            foreach (var p in periods) WorkingHoursPeriods.Add(new WorkingHoursPeriodRow(p));

            foreach (var e in MasterEditor.LoadEmployees(FilePath(MasterSet.EmployeeFileName), messages))
                Employees.Add(new EmployeeRow(e));

            foreach (var d in MasterEditor.LoadHolidays(FilePath(MasterSet.HolidayFileName), messages))
                Holidays.Add(new HolidayRow(d));

            foreach (var f in MasterEditor.LoadApplicationForms(FilePath(MasterSet.ApplicationFormFileName), messages))
                ApplicationForms.Add(new ApplicationFormMappingRow(f));

            var bands = MasterEditor.LoadBreakRule(FilePath(MasterSet.BreakRuleFileName), messages, out var breakSettings);
            foreach (var b in bands) BreakBands.Add(new BreakBandRow(b));
            BreakUnitMinutes = breakSettings.UnitMinutes;
            BreakInRounding = breakSettings.InRounding;
            BreakOutRounding = breakSettings.OutRounding;

            var judgement = MasterEditor.LoadJudgementRule(FilePath(MasterSet.JudgementRuleFileName), messages);
            EarlyInMinutes = judgement.EarlyInMinutes;
            OvertimeMinutes = judgement.OvertimeMinutes;
            FullTimeSpanMinutes = judgement.FullTimeSpanMinutes;
            ToleranceMinutes = judgement.ToleranceMinutes;

            ErrorText = messages.Count > 0
                ? string.Join(Environment.NewLine, messages) + Environment.NewLine +
                  "※ 読めなかったファイルは、保存すると .bak に退避され、画面の内容で作り直されます。"
                : "";

            StatusText = $"従業員 {Employees.Count} 名(管理区分オン {ManagedEmployeeCount} 名) / " +
                         $"別名 {Aliases.Count} 件 / 勤務区分 {ShiftTypes.Count} 件 / " +
                         $"所定労働時間 {WorkingHoursDivisions.Count} 所属部 {WorkingHoursPeriods.Count} 区間 / " +
                         $"祝日 {Holidays.Count} 日 / 申請書 {ApplicationForms.Count} 件 / " +
                         $"休憩 {BreakBands.Count} 段階 を読み込みました。";
        }
        catch (Exception ex)
        {
            ErrorText = $"マスタを読み込めません: {ex.Message}";
        }
        finally
        {
            _loading = false;
            IsDirty = false;
            NotifyEmployeeCounts();
            TryRefreshEmployeesView();
        }
    }

    private void Reload()
    {
        Load();
        StatusText = "マスタを読み直しました。" + StatusText;
    }

    /// <summary>突合で未解決だった氏名を、別名マスタの下書きとして取り込む。</summary>
    private void ImportUnresolved()
    {
        var known = new HashSet<string>(Aliases.Select(r => NameNormalizer.Normalize(r.Source)));
        int added = 0;

        foreach (var u in _unresolved.OrderBy(u => u.Origin).ThenBy(u => u.SourceName))
        {
            if (!known.Add(u.NormalizedName)) continue;
            Aliases.Add(new AliasRow
            {
                Source = u.SourceName,
                Note = $"{u.Origin} {u.Occurrences}件 {u.Department}".Trim()
            });
            added++;
        }

        StatusText = added > 0
            ? $"未解決の氏名 {added} 件を追加しました。「正式氏名」を入力してから保存してください。"
            : "追加できる未解決の氏名はありません(すべて登録済みです)。";
    }

    // ================= 保存 =================

    private void Save()
    {
        var errors = Validate();
        if (errors.Count > 0)
        {
            ErrorText = string.Join(Environment.NewLine, errors);

            // 管理区分で絞り込んでいると、誤りのある行が画面に出ていないことがある
            if (!ShowAllEmployees && Employees.Any(e => !e.Managed))
                ErrorText += Environment.NewLine +
                             "※ 管理区分のチェックが外れている行は従業員タブに出ていません。" +
                             "「対象外も表示」にチェックを入れると確認できます。";

            StatusText = $"入力に誤りがあるため保存していません({errors.Count} 件)。";
            return;
        }

        try
        {
            System.IO.Directory.CreateDirectory(Directory);

            MasterEditor.SaveAliases(FilePath(MasterSet.AliasFileName), Aliases.Select(r => r.ToEntry()));
            MasterEditor.SaveShiftTypes(FilePath(MasterSet.ShiftTypeFileName), ShiftTypes.Select(r => r.ToEntry()));
            MasterEditor.SaveWorkingHours(FilePath(MasterSet.WorkingHoursFileName),
                WorkingHoursDivisions.Select(r => r.ToEntry()), WorkingHoursPeriods.Select(r => r.ToEntry()));
            MasterEditor.SaveEmployees(FilePath(MasterSet.EmployeeFileName), Employees.Select(r => r.ToEntry()));
            MasterEditor.SaveHolidays(FilePath(MasterSet.HolidayFileName), Holidays.Select(r => r.ToEntry()));
            MasterEditor.SaveApplicationForms(FilePath(MasterSet.ApplicationFormFileName),
                ApplicationForms.Select(r => r.ToEntry()));
            MasterEditor.SaveBreakRule(FilePath(MasterSet.BreakRuleFileName), BreakBands.Select(r => r.ToEntry()),
                new BreakRuleSettings(BreakUnitMinutes, BreakInRounding, BreakOutRounding));
            MasterEditor.SaveJudgementRule(FilePath(MasterSet.JudgementRuleFileName),
                new JudgementRuleSettings(EarlyInMinutes, OvertimeMinutes, FullTimeSpanMinutes, ToleranceMinutes));

            Saved = true;
            IsDirty = false;
            ErrorText = "";
            StatusText = $"保存しました({DateTime.Now:HH:mm:ss})。" +
                         $"上書き前の内容は同じ場所の *{MasterEditor.BackupExtension} に残しています。" +
                         "突合結果へは、次に「突合実行」を押したときから反映されます。";
        }
        catch (Exception ex)
        {
            ErrorText = $"保存できません: {ex.Message}";
            StatusText = "保存に失敗しました。マスタのファイルが他のアプリで開かれていないか確認してください。";
        }
    }

    /// <summary>保存できる内容かを確かめる。1件でも誤りがあれば保存しない。</summary>
    private List<string> Validate()
    {
        var errors = new List<string>();

        var aliasSeen = new HashSet<string>();
        for (int i = 0; i < Aliases.Count; i++)
        {
            var row = Aliases[i];
            var source = row.Source.Trim();
            if (source.Length == 0)
            {
                errors.Add($"別名マスタ {i + 1} 行目: 「表記」は必須です。");
                continue;
            }
            if (!aliasSeen.Add(NameNormalizer.Normalize(source)))
                errors.Add($"別名マスタ {i + 1} 行目: 表記「{source}」が重複しています。");
        }

        var codeSeen = new HashSet<string>();
        for (int i = 0; i < ShiftTypes.Count; i++)
        {
            var row = ShiftTypes[i];
            var code = row.Code.Trim();
            if (code.Length == 0)
            {
                errors.Add($"勤務区分マスタ {i + 1} 行目: 「区分」は必須です。");
                continue;
            }
            if (!codeSeen.Add(NameNormalizer.Normalize(code)))
                errors.Add($"勤務区分マスタ {i + 1} 行目: 区分「{code}」が重複しています。");
        }

        var divisionSeen = new HashSet<string>();
        for (int i = 0; i < WorkingHoursDivisions.Count; i++)
        {
            var row = WorkingHoursDivisions[i];
            var name = row.Name.Trim();
            if (name.Length == 0)
            {
                errors.Add($"所定労働時間マスタ(所属部) {i + 1} 行目: 「所属部」は必須です。");
                continue;
            }
            if (!divisionSeen.Add(name))
                errors.Add($"所定労働時間マスタ(所属部) {i + 1} 行目: 所属部「{name}」が重複しています。");

            foreach (var (label, text) in new[] { ("平日", row.WeekdayBreak), ("土日祝", row.HolidayBreak) })
            {
                var value = text.Trim();
                if (value.Length == 0 || !int.TryParse(value, out var minutes) || minutes is < 0 or > 600)
                    errors.Add($"所定労働時間マスタ(所属部) {i + 1} 行目: {label}の休憩「{text}」は 0〜600 の分で入力してください(例 90)。");
            }
        }

        var periodSeen = new HashSet<(string, string, string)>();
        for (int i = 0; i < WorkingHoursPeriods.Count; i++)
        {
            var row = WorkingHoursPeriods[i];
            var division = row.Division.Trim();
            if (division.Length == 0)
            {
                errors.Add($"所定労働時間マスタ(期間) {i + 1} 行目: 「所属部」は必須です。");
                continue;
            }
            if (!divisionSeen.Contains(division))
                errors.Add($"所定労働時間マスタ(期間) {i + 1} 行目: 所属部「{division}」が上の一覧にありません。");

            if (!WorkingHoursMaster.TryParseMonthDay(row.From, out _))
                errors.Add($"所定労働時間マスタ(期間) {i + 1} 行目: 開始「{row.From}」を読めません(例 4/1)。");
            if (!WorkingHoursMaster.TryParseMonthDay(row.To, out _))
                errors.Add($"所定労働時間マスタ(期間) {i + 1} 行目: 終了「{row.To}」を読めません(例 6/30)。");

            if (WorkingHoursMaster.ParseHours(row.Weekday.Trim()) == null)
                errors.Add($"所定労働時間マスタ(期間) {i + 1} 行目: 平日の所定「{row.Weekday}」を読めません(例 8 / 7.5)。");
            if (row.Holiday.Trim().Length > 0 && WorkingHoursMaster.ParseHours(row.Holiday.Trim()) == null)
                errors.Add($"所定労働時間マスタ(期間) {i + 1} 行目: 土日祝の所定「{row.Holiday}」を読めません(例 8.5)。");

            // 期間の休憩は空欄可(空欄なら所属部の既定を使う)
            foreach (var (label, text) in new[] { ("平日", row.WeekdayBreak), ("土日祝", row.HolidayBreak) })
            {
                var value = text.Trim();
                if (value.Length > 0 && (!int.TryParse(value, out var minutes) || minutes is < 0 or > 600))
                    errors.Add($"所定労働時間マスタ(期間) {i + 1} 行目: {label}の休憩「{text}」は 0〜600 の分で入力してください" +
                               "(空欄なら所属部の休憩を使います)。");
            }

            if (!periodSeen.Add((division, row.From.Trim(), row.To.Trim())))
                errors.Add($"所定労働時間マスタ(期間) {i + 1} 行目: 「{division}」の {row.From.Trim()}〜{row.To.Trim()} が重複しています。");
        }

        // ---- 従業員マスタ(氏名と所属部は必須) ----
        var employeeSeen = new HashSet<string>();
        var knownDivisions = WorkingHoursDivisions.Select(d => d.Name.Trim()).Where(n => n.Length > 0).ToHashSet();
        var unknownDivisions = new HashSet<string>();

        for (int i = 0; i < Employees.Count; i++)
        {
            var row = Employees[i];
            var name = row.Name.Trim();
            if (name.Length == 0)
            {
                errors.Add($"従業員マスタ {i + 1} 行目: 「氏名」は必須です。");
                continue;
            }
            if (!employeeSeen.Add(NameNormalizer.Normalize(name)))
                errors.Add($"従業員マスタ {i + 1} 行目: 氏名「{name}」が重複しています。");

            if (row.Division.Trim().Length == 0)
                errors.Add($"従業員マスタ {i + 1} 行目: 「{name}」の「所属部」は必須です。");
            else if (knownDivisions.Count > 0 && !knownDivisions.Contains(row.Division.Trim()))
                unknownDivisions.Add(row.Division.Trim());

            if (!EmployeeMaster.TryParseEmployment(row.Employment, out _))
                errors.Add($"従業員マスタ {i + 1} 行目: 雇用区分「{row.Employment}」は 正社員 / パート / アルバイト のいずれかにしてください。");

            if (row.WorkHours.Trim().Length > 0 && !EmployeeMaster.TryParseWorkHours(row.WorkHours, out _))
                errors.Add($"従業員マスタ {i + 1} 行目: 1日の拘束時間「{row.WorkHours}」を読めません(例 9:00 または 9)。");

            var wage = row.HourlyWage.Trim();
            if (wage.Length > 0 && (!int.TryParse(wage, out var wageValue) || wageValue is <= 0 or >= 100000))
                errors.Add($"従業員マスタ {i + 1} 行目: 時給「{row.HourlyWage}」は 1〜99999 の数値で入力してください(空欄可)。");

            foreach (var (label, text) in new[] { ("入社日", row.Joined), ("退職日", row.Left) })
                if (text.Trim().Length > 0 && !DateOnly.TryParse(text.Trim(), out _))
                    errors.Add($"従業員マスタ {i + 1} 行目: {label}「{text}」を読めません(例 2026-04-01)。");
        }

        // 所定労働時間マスタに無い所属部は、拘束時間での判定になるため知らせる(保存は止めない)
        if (unknownDivisions.Count > 0)
            StatusText = $"※ 所定労働時間マスタに無い所属部があります({string.Join(" , ", unknownDivisions.Order())})。" +
                         "この所属部の方は、判定閾値マスタの拘束時間で判定します。";

        // ---- 祝日マスタ ----
        var daySeen = new HashSet<int>();
        for (int i = 0; i < Holidays.Count; i++)
        {
            var row = Holidays[i];
            var date = row.Date.Trim();
            if (date.Length == 0)
            {
                errors.Add($"祝日マスタ {i + 1} 行目: 「月/日」は必須です。");
                continue;
            }
            if (!HolidayMaster.TryParseMonthDay(date, out var monthDay))
                errors.Add($"祝日マスタ {i + 1} 行目: 「{date}」を読めません(例 7/20)。");
            else if (!daySeen.Add(monthDay))
                errors.Add($"祝日マスタ {i + 1} 行目: 「{date}」が重複しています。");

            if (!HolidayMaster.TryParseKind(row.Kind, out _))
                errors.Add($"祝日マスタ {i + 1} 行目: 区分「{row.Kind}」は 祝 / 休場 のいずれかにしてください。");
        }

        // ---- 申請書マスタ ----
        for (int i = 0; i < ApplicationForms.Count; i++)
        {
            var row = ApplicationForms[i];
            if (row.Code.Trim().Length == 0)
                errors.Add($"申請書マスタ {i + 1} 行目: 「判定コード」は必須です。");
            if (row.FormName.Trim().Length == 0)
                errors.Add($"申請書マスタ {i + 1} 行目: 「申請書」は必須です。");
        }

        // ---- 休憩ルール ----
        if (!int.TryParse(BreakUnitMinutes.Trim(), out var unit) || unit is < 1 or > 60)
            errors.Add($"休憩ルールマスタ: 丸めの単位「{BreakUnitMinutes}」は 1〜60 の分で入力してください(例 15)。");

        foreach (var (label, value) in new[] { ("出勤", BreakInRounding), ("退勤", BreakOutRounding) })
            if (!new[] { "up", "down", "nearest" }.Contains(value.Trim().ToLowerInvariant()))
                errors.Add($"休憩ルールマスタ: {label}の丸め方「{value}」は up / down / nearest のいずれかにしてください。");

        if (BreakBands.Count == 0) errors.Add("休憩ルールマスタ: 段階を1行以上入れてください。");
        for (int i = 0; i < BreakBands.Count; i++)
        {
            var row = BreakBands[i];
            var upTo = row.UpToHours.Trim();
            if (upTo.Length > 0 &&
                (!double.TryParse(upTo, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) || h is <= 0 or > 24))
                errors.Add($"休憩ルールマスタ {i + 1} 行目: 上限「{upTo}」は 0 より大きく 24 以下の時間で入力してください(空欄=それ以上すべて)。");

            if (!int.TryParse(row.BreakMinutes.Trim(), out var minutes) || minutes is < 0 or > 600)
                errors.Add($"休憩ルールマスタ {i + 1} 行目: 休憩「{row.BreakMinutes}」は 0〜600 の分で入力してください。");
        }

        // ---- 判定閾値マスタ ----
        foreach (var (label, text, max) in new[]
                 {
                     ("早出とみなす分", EarlyInMinutes, 600),
                     ("時間外とみなす分", OvertimeMinutes, 600),
                     ("拘束時間(分)", FullTimeSpanMinutes, 1440),
                     ("遅刻・早退の許容(分)", ToleranceMinutes, 600),
                 })
        {
            if (!int.TryParse(text.Trim(), out var value) || value < 0 || value > max)
                errors.Add($"判定閾値マスタ: {label}「{text}」は 0〜{max} の分で入力してください。");
        }

        return errors;
    }

    // ================= 変更の監視 =================

    private static T AddRow<T>(ObservableCollection<T> rows, T row)
    {
        rows.Add(row);
        return row;
    }

    private static void Remove<T>(ObservableCollection<T> rows, T? row) where T : class
    {
        if (row != null) rows.Remove(row);
    }

    /// <summary>行の追加・削除と、各行の編集を「未保存の変更」として拾う。</summary>
    private void Watch<T>(ObservableCollection<T> rows) where T : ObservableObject
    {
        rows.CollectionChanged += (_, e) =>
        {
            foreach (var row in Items<T>(e.OldItems)) row.PropertyChanged -= OnRowChanged;
            foreach (var row in Items<T>(e.NewItems)) row.PropertyChanged += OnRowChanged;
            MarkDirty();
        };
    }

    private static IEnumerable<T> Items<T>(System.Collections.IList? items) =>
        items?.Cast<T>() ?? Enumerable.Empty<T>();

    private void OnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        MarkDirty();

        // 管理区分を切り替えたら、その場で従業員タブの絞り込みをやり直す
        if (sender is EmployeeRow && e.PropertyName == nameof(EmployeeRow.Managed))
            RefreshEmployeesView();
    }

    private void MarkDirty()
    {
        if (_loading) return;
        IsDirty = true;
    }
}
