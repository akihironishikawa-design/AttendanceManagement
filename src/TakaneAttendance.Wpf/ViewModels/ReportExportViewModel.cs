using System.Collections.ObjectModel;
using System.IO;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Reporting;

namespace TakaneAttendance.Wpf.ViewModels;

/// <summary>出力する帳票1件の選択状態(統合仕様書 v3.0 第9章 OUT-001)。</summary>
public sealed class ReportChoice : ObservableObject
{
    public required string Name { get; init; }
    /// <summary>この帳票が何を出すかの説明。画面に併記する。</summary>
    public required string Description { get; init; }
    /// <summary>テンプレート原本が要るか。要る場合はパスを指定してもらう。</summary>
    public required bool NeedsTemplate { get; init; }
    /// <summary>出力ファイル名(対象年月を差し込む)。</summary>
    public required string FileNameFormat { get; init; }

    private bool _selected = true;
    public bool Selected { get => _selected; set => Set(ref _selected, value); }

    /// <summary>テンプレートのパス(要る帳票のみ)。</summary>
    private string _templatePath = "";
    public string TemplatePath { get => _templatePath; set => Set(ref _templatePath, value); }

    /// <summary>テンプレートが要るのに未指定なら出力できない。</summary>
    public bool IsReady => !NeedsTemplate || (TemplatePath.Length > 0 && File.Exists(TemplatePath));

    /// <summary>業務フロー上の位置づけ(提供範囲の設定から)。画面のツールチップに出す。</summary>
    public string FlowNote { get; set; } = "";

    /// <summary>画面に出す状態の文言。</summary>
    public string StateText =>
        !NeedsTemplate ? "テンプレート不要"
        : TemplatePath.Length == 0 ? "テンプレート未指定"
        : File.Exists(TemplatePath) ? "準備できています"
        : "テンプレートが見つかりません";

    public string FileNameFor(int year, int month) => string.Format(FileNameFormat, year, month);
}

/// <summary>出力履歴の1件(統合仕様書 v3.0 第9章 OUT-007)。</summary>
public sealed class ReportHistoryEntry
{
    public required DateTime OutputAt { get; init; }
    public required string ReportName { get; init; }
    public required string Period { get; init; }
    public required string OutputBy { get; init; }
    public required string Path { get; init; }
    public required bool Success { get; init; }
    public string Detail { get; init; } = "";

    public string TimeText => OutputAt.ToString("MM/dd HH:mm:ss");
    public string ResultText => Success ? "成功" : "失敗";
}

/// <summary>
/// 帳票出力画面の状態(統合仕様書 v3.0 第9章)。
///
/// どの帳票を出すか、テンプレートはどれか、どこへ出すかをここで持ち、
/// 出力前に対象者・件数をプレビューできるようにする。
/// 1つの帳票の出力に失敗しても、他の帳票の出力は続ける(仕様書 第19章)。
/// </summary>
public sealed class ReportExportViewModel : ObservableObject
{
    public const string AttendanceReport = "出席記録レポート";
    public const string ApplicationForms = "申請書 確認一覧";
    public const string AttendanceBook = "出勤簿";
    public const string PartTimePayroll = "パート・アルバイト給与計算表";
    public const string DailySummary = "日別集計表(部門別)";
    public const string EditHistoryList = "修正履歴一覧";
    public const string PunchDetail = "打刻詳細一覧";

    /// <summary>
    /// 実装している帳票の全一覧。
    /// このうち提供範囲(report_scope.xml)に入っているものだけを <see cref="Reports"/> に出す。
    /// </summary>
    /// <summary>同梱している出席記録レポートの様式。無ければ空文字(出力時に選んでもらう)。</summary>
    private static string DefaultAttendanceReportTemplate
    {
        get
        {
            var path = BundledTemplate.PathOf(@"templates\出席記録レポート.xlsx");
            return File.Exists(path) ? path : "";
        }
    }

    private static IReadOnlyList<ReportChoice> BuildAll() => new List<ReportChoice>
    {
        new ReportChoice
        {
            Name = AttendanceReport,
            Description = "予定シフト・実打刻・判定。画面に表示されている内容をそのまま出力します",
            NeedsTemplate = true,
            // 同梱の様式を初期値にする(見つからない場合だけ、出力時に選んでもらう)
            TemplatePath = DefaultAttendanceReportTemplate,
            FileNameFormat = "出席記録レポート_{0}{1:00}.xlsx"
        },
        new ReportChoice
        {
            Name = ApplicationForms,
            Description = "判定から、用意が必要な申請書を一覧にします(業務フロー STEP1 ④)",
            NeedsTemplate = false,
            FileNameFormat = "申請書確認一覧_{0}{1:00}.xlsx"
        },
        new ReportChoice
        {
            Name = AttendanceBook,
            Description = "公・有・欠などの勤務区分を月間一覧で出力します",
            NeedsTemplate = true,
            FileNameFormat = "出勤簿_{0}{1:00}.xls"
        },
        new ReportChoice
        {
            Name = PartTimePayroll,
            Description = "出社・退社・休憩を書き込みます。パート・アルバイトのみが対象です",
            NeedsTemplate = true,
            FileNameFormat = "パートアルバイト給与計算表_{0}{1:00}.xlsx"
        },
    };

    /// <summary>画面に出す帳票。提供範囲の設定で絞り込んだもの。</summary>
    public ObservableCollection<ReportChoice> Reports { get; } = new(BuildAll());

    /// <summary>出力履歴(新しいものが上)。</summary>
    public ObservableCollection<ReportHistoryEntry> History { get; } = new();

    private string _outputDirectory = "";
    /// <summary>出力先フォルダ。実行のたびに日時別のサブフォルダを作る(仕様書 OUT-005)。</summary>
    public string OutputDirectory
    {
        get => _outputDirectory;
        set => Set(ref _outputDirectory, value);
    }

    private string _departmentFilter = "";
    /// <summary>部門の絞り込み。空なら全体(仕様書 OUT-003)。</summary>
    public string DepartmentFilter
    {
        get => _departmentFilter;
        // 一覧を差し替えるとコンボボックスが null を書き戻してくるため、空文字に直す
        set => Set(ref _departmentFilter, value ?? "");
    }

    private IReadOnlyList<string> _departments = new List<string> { "" };
    /// <summary>部門の選択肢。先頭の空文字は「全体」。</summary>
    public IReadOnlyList<string> Departments
    {
        get => _departments;
        private set => Set(ref _departments, value);
    }

    private string _previewText = "突合を実行すると、出力の対象がここに出ます。";
    public string PreviewText { get => _previewText; set => Set(ref _previewText, value); }

    public IEnumerable<ReportChoice> SelectedReports => Reports.Where(r => r.Selected);

    public void SelectAll(bool selected)
    {
        foreach (var r in Reports) r.Selected = selected;
    }

    /// <summary>部門の一覧を突合結果から作り直す。</summary>
    public void UpdateDepartments(MatchingResult? result)
    {
        var wanted = new List<string> { "" };   // 空 = 全体
        if (result != null)
            wanted.AddRange(result.Details.Select(x => x.Department)
                                          .Where(x => x.Length > 0)
                                          .Distinct().OrderBy(x => x));

        // 中身が変わっていなければ触らない。この処理は選択変更からも呼ばれるため、
        // 呼ばれるたびに作り直すとコンボボックスの表示が乱れる
        if (Departments.SequenceEqual(wanted)) return;

        // 中身を入れ替えるのではなく、一覧そのものを差し替える。
        // ObservableCollection を Clear して詰め直すと、コンボボックスに前の項目が
        // 残ったままになり、開くたびに項目が増えて見える
        var current = DepartmentFilter;
        Departments = wanted;
        DepartmentFilter = wanted.Contains(current) ? current : "";
    }

    /// <summary>出力前のプレビュー(仕様書 OUT-006)。対象者・件数・帳票名を出す。</summary>
    public void UpdatePreview(MatchingResult? result, ReportSheet? sheet)
    {
        if (result == null || sheet == null)
        {
            PreviewText = "突合を実行すると、出力の対象がここに出ます。";
            return;
        }

        var target = result.Details.AsEnumerable();
        if (DepartmentFilter.Length > 0) target = target.Where(d => d.Department == DepartmentFilter);
        var details = target.ToList();

        int persons = details.Select(d => d.Person.Key).Distinct().Count();
        int partTime = details.Where(d => Core.Masters.EmployeeMaster.IsPartTimePayroll(d.Person.Employment))
                              .Select(d => d.Person.Key).Distinct().Count();

        var names = SelectedReports.Select(r => r.Name).ToList();
        var notReady = SelectedReports.Where(r => !r.IsReady).Select(r => r.Name).ToList();

        PreviewText =
            $"対象期間 : {result.TargetYear}年{result.TargetMonth}月" +
            $"　部門 : {(DepartmentFilter.Length == 0 ? "全体" : DepartmentFilter)}\n" +
            $"対象者 : {persons} 名(うちパート・アルバイト {partTime} 名)　明細 {details.Count} 件" +
            $"　修正履歴 {sheet.History.Count} 件\n" +
            $"出力する帳票 : {(names.Count == 0 ? "(選択されていません)" : string.Join(" / ", names))}" +
            (notReady.Count > 0 ? $"\n※ テンプレート未指定のため出力できません : {string.Join(" / ", notReady)}" : "");
    }

    public void AddHistory(string reportName, string period, string path, bool success, string detail)
        => History.Insert(0, new ReportHistoryEntry
        {
            OutputAt = DateTime.Now,
            ReportName = reportName,
            Period = period,
            OutputBy = Environment.UserName,
            Path = path,
            Success = success,
            Detail = detail
        });
}
