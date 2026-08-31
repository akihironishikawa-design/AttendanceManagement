using System.Text;
using System.Xml.Linq;
using TakaneAttendance.Core;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Matching;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Parsing;
using TakaneAttendance.Core.Reporting;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine("""
        勤怠突合 確認用コンソール

        使い方:
          sheets <file>                                    シート一覧を表示
          gen-testdata <outDir>                            結合テスト用データ一式を生成
          run <shift.xls> <shiftSheet> <punch.xls> <punchSheet> [options]
          draft <保留ファイル.kintai.json> [--report <template.xlsx> <output.xlsx>]
                                                           保留ファイルを読み、続きの状態を確認・出力する
          breaktime [<出勤> <退勤> ...]                    休憩ルール(15分丸め)の確認。省略時は既定の例で表示
          accept [<mastersDir>]                            統合仕様書 v3.0 の受入条件・受入テストを実行する
          import-holidays <年間カレンダー.xlsx> [-o <out.xml>] [--sheet <名>] [--years <から> <まで>]
                                                           祝日マスタ(holiday.xml)を生成する
          import-employees <従業員データ.xlsx> [<パート給与計算表.xlsx>] [-o <out.xml>] [--sheet <名>]
                                                           従業員マスタ(employee.xml)を生成する
          peek <file> [<sheet>] [<rows>] [<cols>] [--formula]
                                                           ブックの中身をそのまま表示する(様式の確認用)
          compare-report <期待.xls> <シート> <実際.xlsx> <シート> [--limit <n>]
                                                           手作業で作られた出席記録と本システムの出力を突き合わせる
          master-roundtrip [<mastersDir>] [<作業用dir>]     マスタ編集画面の読み書きを画面を開かずに確かめる

        options:
          --only-shift            シフト表に載っている社員のみ対象にする
          --report <template.xlsx> <output.xlsx>   出席記録レポートを出力する
          --draft-out <file>      突合結果を保留ファイル(.kintai.json)として書き出す
          --draft-excel <file>    突合結果を一時保存ファイル(Excel)として書き出す
          --forms-out <file.xlsx> 申請書 確認一覧を出力する(業務フロー STEP1 ④)
          --punch2 <file>         2ファイル目以降の打刻データ(複数拠点分。複数回指定できる)
          --out-dir <dir>         帳票の出力先。日別集計表・修正履歴一覧・打刻詳細一覧を出す
          --book <出勤簿.xls>      出勤簿のテンプレート(--out-dir と併用)
          --payroll <給与計算表.xlsx> パート・アルバイト給与計算表のテンプレート(--out-dir と併用)
          --limit <n>             明細の表示件数(既定 40)
        """);
    return 0;
}

if (args[0] == "sheets")
{
    foreach (var (name, i) in ExcelHelper.SheetNames(args[1]).Select((n, i) => (n, i)))
        Console.WriteLine($"[{i}] {name}");
    return 0;
}

// 年間カレンダー Excel から祝日マスタ(holiday.xml)を作る
if (args[0] == "import-holidays")
{
    var calOut = "masters/holiday.xml";
    string? calSheet = null;
    int fromYear = 2026, toYear = 2027;
    for (int i = 2; i < args.Length; i++)
    {
        if (args[i] == "-o" && i + 1 < args.Length) calOut = args[++i];
        else if (args[i] == "--sheet" && i + 1 < args.Length) calSheet = args[++i];
        else if (args[i] == "--years" && i + 2 < args.Length)
        {
            fromYear = int.Parse(args[++i]);
            toYear = int.Parse(args[++i]);
        }
    }
    return TakaneAttendance.Cli.HolidayImporter.Run(args[1], calSheet, calOut, fromYear, toYear);
}

// 従業員データ Excel から従業員マスタ(employee.xml)を作る
if (args[0] == "import-employees")
{
    var output = "masters/employee.xml";
    string? empSheet = null;
    string? payroll = null;
    for (int i = 3; i < args.Length; i++)
    {
        if (args[i] == "-o" && i + 1 < args.Length) output = args[++i];
        else if (args[i] == "--sheet" && i + 1 < args.Length) empSheet = args[++i];
    }
    if (args.Length > 2 && !args[2].StartsWith('-')) payroll = args[2];
    return TakaneAttendance.Cli.EmployeeImporter.Run(args[1], payroll, output, empSheet);
}

// お預かりした帳票・マスタの様式を確かめる
if (args[0] == "peek")
    return TakaneAttendance.Cli.SheetPeek.Run(
        args[1],
        args.Length > 2 && args[2] != "-" ? args[2] : null,
        args.Length > 3 ? int.Parse(args[3]) : 40,
        args.Length > 4 && args[4] != "--formula" ? int.Parse(args[4]) : 12,
        args.Contains("--formula"));

// マスタ編集画面の読み書き(11マスタ)を、画面を開かずに確かめる
if (args[0] == "master-roundtrip")
{
    var sourceDir = args.Length > 1 ? args[1] : Path.Combine(AppContext.BaseDirectory, "masters");
    var workDir = args.Length > 2 ? args[2] : Path.Combine(Path.GetTempPath(), "kintai_master_roundtrip");
    return TakaneAttendance.Cli.MasterRoundTrip.Run(sourceDir, workDir);
}

// お客様が手作業で作られた出席記録と、本システムの出力を突き合わせる
if (args[0] == "compare-report")
{
    if (args.Length < 5)
    {
        Console.Error.WriteLine("使い方: compare-report <期待.xls> <シート> <実際.xlsx> <シート> [--limit <n>]");
        return 1;
    }
    int compareLimit = 40;
    for (int i = 5; i < args.Length - 1; i++)
        if (args[i] == "--limit") compareLimit = int.Parse(args[i + 1]);
    return TakaneAttendance.Cli.CompareReport.Run(args[1], args[2], args[3], args[4], compareLimit);
}

// 統合仕様書 v3.0 第21章(受入条件)・付録B(受入テスト主要ケース)の確認
if (args[0] == "accept")
    return TakaneAttendance.Cli.AcceptanceTests.Run(args.Length > 1 ? args[1] : null);

if (args[0] == "gen-testdata")
    return TakaneAttendance.Cli.TestDataGenerator.Run(
        args.Length > 1 ? args[1] : "testdata",
        args.Length > 2 ? args[2] : null,     // シフト表の見本(省略時 sample/シフト表サンプル.xls)
        args.Length > 3 ? args[3] : null);    // 打刻データの見本(省略時 sample/打刻データサンプル.xlsx)

// 休憩ルール(B-04) と 15分丸め(B-05) の確認。出勤・退勤の組を並べて渡す
if (args[0] == "breaktime")
{
    var rule = BreakRuleMaster.Load(Path.Combine(AppContext.BaseDirectory, "masters", MasterSet.BreakRuleFileName));
    foreach (var msg in rule.Messages) Console.WriteLine("  " + msg);

    Console.WriteLine("================ 休憩ルール ================");
    Console.WriteLine($"  丸め : {rule.UnitMinutes}分 (出勤 {rule.InRounding} / 退勤 {rule.OutRounding})");
    foreach (var band in rule.Bands) Console.WriteLine($"  帯   : {band.Describe()}");

    var pairs = args.Skip(1).ToList();
    if (pairs.Count == 0)
        pairs = new List<string> { "7:00", "13:00", "7:00", "13:15", "7:00", "15:01" };   // 提示いただいた例

    Console.WriteLine();
    Console.WriteLine($"{"打刻",-16}{"丸め後",-18}{"拘束",-8}{"休憩",-8}{"実労働",-8}{"適用した帯"}");
    for (int i = 0; i + 1 < pairs.Count; i += 2)
    {
        if (!TimeText.TryParse(pairs[i], out var inTime) ||
            !TimeText.TryParse(pairs[i + 1], out var outTime))
        {
            Console.Error.WriteLine($"時刻として読めません: {pairs[i]} {pairs[i + 1]}");
            continue;
        }

        var wt = rule.Calculate(inTime, outTime);
        var punched = $"{WorkTime.Hm(inTime)}-{WorkTime.Hm(outTime)}";
        if (wt == null) { Console.WriteLine($"{punched,-16}(退勤が出勤より前のため計算できません)"); continue; }

        Console.WriteLine($"{punched,-16}{wt.RoundedRangeText,-18}{wt.SpanText,-8}{wt.BreakText,-8}{wt.WorkText,-8}{wt.AppliedBand}");
    }
    return 0;
}

// 保留ファイルを読み、続きから編集できる状態を確認する(画面と同じ復元処理を通す)
if (args[0] == "draft")
{
    if (args.Length < 2) { Console.Error.WriteLine("保留ファイルを指定してください。"); return 1; }

    string? draftTemplate = null, draftOut = null;
    for (int i = 2; i < args.Length; i++)
        if (args[i] == "--report") { draftTemplate = args[++i]; draftOut = args[++i]; }

    DraftDocument draft;
    try
    {
        draft = DraftFile.Load(args[1]);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"保留ファイルを開けません: {ex.Message}");
        return 1;
    }

    var draftMasters = MasterSet.Load(draft.Inputs.MastersDirectory is { Length: > 0 } d ? d : null);
    var draftJudge = new ReportJudge(draftMasters,
        new MatchingOptions { OnlyPersonsInShift = draft.Inputs.OnlyPersonsInShift });
    var draftSheet = draft.ToSheet(draftJudge, out int changedJudgements);

    Console.WriteLine("================ 保留ファイル ================");
    Console.WriteLine($"形式         : 第{draft.FormatVersion}版");
    Console.WriteLine($"保存日時     : {draft.SavedAt}");
    Console.WriteLine($"対象年月     : {draftSheet.Year}年{draftSheet.Month}月 ({draftSheet.DayCount}日)");
    Console.WriteLine($"社員         : {draftSheet.Employees.Count} 名");
    Console.WriteLine($"編集済みセル : {draftSheet.EditedCellCount} (保留時 {draft.EditedCellCount})");
    Console.WriteLine($"要確認セル   : {draftSheet.AttentionCellCount}");
    Console.WriteLine($"判定の変化   : {changedJudgements} セル (現在のマスタで計算し直した結果)");
    Console.WriteLine($"シフト表     : {draft.Inputs.ShiftPath} [{draft.Inputs.ShiftSheetName}]");
    Console.WriteLine($"打刻データ   : {draft.Inputs.PunchPath} [{draft.Inputs.PunchSheetName}]");

    var editedCells = draftSheet.Employees
        .SelectMany(e => Enumerable.Range(0, draftSheet.DayCount)
            .Where(i => e.ShiftEdited[i] || e.PunchEdited[i])
            .Select(i => (Employee: e, Day: i + 1)))
        .ToList();

    if (editedCells.Count > 0)
    {
        Console.WriteLine();
        Console.WriteLine("================ 画面で編集したセル ================");
        foreach (var (employee, day) in editedCells)
            Console.WriteLine($"  {draftSheet.Month}/{day,-3}{Pad(employee.Name, 16)}" +
                              $"シフト={employee.Shift[day - 1],-10} 打刻={employee.Punch[day - 1],-14} " +
                              $"{employee.Judgements[day - 1].Note}");
    }

    if (draftTemplate != null && draftOut != null)
    {
        Console.WriteLine();
        Console.WriteLine("================ 出席記録レポート出力 ================");
        var draftWriter = new AttendanceReportWriter();
        var draftReport = draftWriter.Write(draftSheet, draftTemplate, draftOut);
        Console.WriteLine($"  出力先       : {draftOut}");
        Console.WriteLine($"  出力社員数   : {draftReport.TotalEmployees} 名");
        Console.WriteLine($"  書込セル数   : シフト {draftReport.WrittenShiftCells} / 打刻 {draftReport.WrittenPunchCells}");
        foreach (var m in draftReport.Messages) Console.WriteLine("  " + m);
    }

    return 0;
}

if (args[0] != "run") { Console.Error.WriteLine("不明なコマンドです。"); return 1; }

string shiftPath = args[1], shiftSheet = args[2], punchPath = args[3], punchSheet = args[4];
var options = new MatchingOptions();
double? workHours = null;
string? templatePath = null, reportOut = null, draftOutPath = null, formsOutPath = null;
string? outDir = null, bookTemplate = null, payrollTemplate = null, draftExcelOut = null;
var extraPunchPaths = new List<string>();
int limit = 40;

for (int i = 5; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--only-shift": options.OnlyPersonsInShift = true; break;
        case "--workhours": workHours = double.Parse(args[++i]); break;
        case "--report": templatePath = args[++i]; reportOut = args[++i]; break;
        case "--draft-out": draftOutPath = args[++i]; break;
        case "--draft-excel": draftExcelOut = args[++i]; break;
        case "--forms-out": formsOutPath = args[++i]; break;
        case "--punch2": extraPunchPaths.Add(args[++i]); break;
        case "--out-dir": outDir = args[++i]; break;
        case "--book": bookTemplate = args[++i]; break;
        case "--payroll": payrollTemplate = args[++i]; break;
        case "--limit": limit = int.Parse(args[++i]); break;
    }
}

string mastersDir = Path.Combine(AppContext.BaseDirectory, "masters");
Directory.CreateDirectory(mastersDir);

var service = new MatchingService();
var result = service.Execute(new MatchingRequest
{
    ShiftPath = shiftPath,
    ShiftSheetName = shiftSheet,
    PunchPath = punchPath,
    PunchSheetName = punchSheet,
    AdditionalPunchPaths = extraPunchPaths,
    MastersDirectory = mastersDir,
    Options = options
});

Console.WriteLine("================ 実行情報 ================");
Console.WriteLine($"execution_id : {result.ExecutionId}");
Console.WriteLine($"対象年月     : {result.TargetYear}年{result.TargetMonth}月");
Console.WriteLine($"処理時間     : {result.Elapsed.TotalMilliseconds:0} ms");
foreach (var m in result.Messages) Console.WriteLine("  " + m);

if (result.HasFatalError)
{
    Console.WriteLine();
    Console.WriteLine("================ 処理停止 ================");
    foreach (var m in result.ProcessMessages.Where(m => m.Level == MessageLevel.Fatal))
        Console.WriteLine("  " + m);
    Console.WriteLine();
    Console.WriteLine("  入力を修正して取り込み直してください。");
    return 1;
}

Console.WriteLine();
Console.WriteLine("================ サマリー ================");
Console.WriteLine($"シフト読込 : {result.ShiftRecordCount,6} 件");
Console.WriteLine($"打刻読込   : {result.PunchRecordCount,6} 件");
Console.WriteLine($"突合明細   : {result.Details.Count,6} 件 / 対象人数 {result.PersonCount} 名");
Console.WriteLine($"  正常     : {result.NormalCount,6} 件");
Console.WriteLine($"  遅刻     : {result.LateCount,6} 件");
Console.WriteLine($"  早退     : {result.EarlyLeaveCount,6} 件");
Console.WriteLine($"  早出     : {result.EarlyInCount,6} 件");
Console.WriteLine($"  時間外   : {result.OvertimeCount,6} 件");
Console.WriteLine($"  要確認   : {result.ReviewCount,6} 件");
Console.WriteLine($"  対象外   : {result.ExcludedCount,6} 件");

Console.WriteLine();
Console.WriteLine("================ 判定コード別 ================");
foreach (var g in result.Details.SelectMany(d => d.ResultCodes)
                                .GroupBy(c => c)
                                .OrderByDescending(g => g.Count()))
    Console.WriteLine($"  {ResultCodeInfo.CodeName(g.Key),-20} {g.Count(),5} 件  " +
                      $"[{ResultCodeInfo.Label(g.Key)}] {ResultCodeInfo.Description(g.Key)}");

if (result.UnresolvedNames.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("================ 氏名未解決(別名マスタ要登録) ================");
    foreach (var u in result.UnresolvedNames)
        Console.WriteLine($"  [{u.Origin}] {u.SourceName,-16} 正規化={u.NormalizedName,-14} {u.Occurrences}件 {u.Department}");
}

Console.WriteLine();
Console.WriteLine($"================ 正常以外の明細(先頭{limit}件) ================");
Console.WriteLine($"{"日付",-12}{"曜",-3}{"氏名",-16}{"部門",-14}{"予定",-8}{"1回目",-8}{"最終",-8}{"主判定",-8}{"内訳"}");
foreach (var d in result.Details.Where(d => d.Judgement != Judgement.Normal)
                                .OrderByDescending(d => d.Judgement)
                                .ThenBy(d => d.WorkDate)
                                .Take(limit))
    Console.WriteLine($"{d.DateText,-12}{d.DayOfWeekText,-3}{Pad(d.PersonName, 16)}{Pad(d.Department, 14)}" +
                      $"{d.ShiftText,-8}{d.FirstPunchText,-8}{d.LastPunchText,-8}{Pad(d.JudgementLabel, 8)}{d.ResultText}");

// 勤務時間(15分丸め + 休憩の自動計算)。通常勤務で出退勤が揃った日だけが対象。
var worked = result.Details.Where(d => d.WorkTime != null).ToList();
if (worked.Count > 0)
{
    Console.WriteLine();
    Console.WriteLine("================ 勤務時間(15分丸め・休憩自動計算) ================");
    Console.WriteLine($"{"氏名",-18}{"日数",6}{"拘束計",10}{"休憩計",10}{"実労働計",10}");
    foreach (var g in worked.GroupBy(d => d.PersonName).OrderBy(g => g.Key))
        Console.WriteLine($"{Pad(g.Key, 18)}{g.Count(),6}" +
                          $"{WorkTime.Hm(g.Sum(d => d.WorkTime!.SpanMinutes)),10}" +
                          $"{WorkTime.Hm(g.Sum(d => d.WorkTime!.BreakMinutes)),10}" +
                          $"{WorkTime.Hm(g.Sum(d => d.WorkTime!.WorkMinutes)),10}");

    Console.WriteLine($"{Pad("合計", 18)}{worked.Count,6}" +
                      $"{WorkTime.Hm(worked.Sum(d => d.WorkTime!.SpanMinutes)),10}" +
                      $"{WorkTime.Hm(worked.Sum(d => d.WorkTime!.BreakMinutes)),10}" +
                      $"{WorkTime.Hm(worked.Sum(d => d.WorkTime!.WorkMinutes)),10}");

    Console.WriteLine();
    Console.WriteLine("  休憩の内訳:");
    foreach (var g in worked.GroupBy(d => d.WorkTime!.BreakMinutes).OrderBy(g => g.Key))
        Console.WriteLine($"    休憩 {WorkTime.Hm(g.Key),-6} : {g.Count(),4} 日");
}

if (templatePath != null && reportOut != null)
{
    Console.WriteLine();
    Console.WriteLine("================ 出席記録レポート出力 ================");
    var writer = new AttendanceReportWriter();
    var rep = writer.Write(result, templatePath, reportOut);
    Console.WriteLine($"  出力先       : {reportOut}");
    Console.WriteLine($"  出力社員数   : {rep.TotalEmployees} 名 (うち突合結果あり {rep.WrittenEmployees} 名)");
    Console.WriteLine($"  書込セル数   : シフト {rep.WrittenShiftCells} / 打刻 {rep.WrittenPunchCells}");
    if (rep.AddedEmployees.Count > 0)
        Console.WriteLine($"  末尾に追加   : {rep.AddedEmployees.Count} 名 ({string.Join(", ", rep.AddedEmployees.Take(5))}{(rep.AddedEmployees.Count > 5 ? " ..." : "")})");
    if (rep.EmptyEmployees.Count > 0)
        Console.WriteLine($"  データなし   : {rep.EmptyEmployees.Count} 名 (テンプレートの社員行は残して空欄で出力)");
    foreach (var m in rep.Messages) Console.WriteLine("  " + m);
}

if (draftOutPath != null)
{
    Console.WriteLine();
    Console.WriteLine("================ 保留ファイル出力 ================");
    var sheet = ReportSheetBuilder.Build(result, templatePath);
    DraftFile.Save(draftOutPath, DraftDocument.FromSheet(sheet, new DraftInputs
    {
        ShiftPath = shiftPath,
        ShiftSheetName = shiftSheet,
        PunchPath = punchPath,
        PunchSheetName = punchSheet,
        TemplatePath = templatePath ?? "",
        MastersDirectory = mastersDir,
        AutoDetectYearMonth = true,
        OnlyPersonsInShift = options.OnlyPersonsInShift
    }, result.Messages));

    Console.WriteLine($"  出力先     : {draftOutPath}");
    Console.WriteLine($"  社員       : {sheet.Employees.Count} 名 / {sheet.DayCount} 日");
    Console.WriteLine($"  要確認セル : {sheet.AttentionCellCount}");
}

// 申請書 確認一覧(勤怠締め業務フロー STEP1 ④「申請書を印刷」)
{
    var formMaster = result.Masters?.ApplicationForms;
    var formRows = formMaster == null
        ? new List<ApplicationFormRow>()
        : ApplicationFormReport.Build(result, formMaster);

    Console.WriteLine();
    Console.WriteLine("================ 申請書 確認一覧 ================");
    if (formRows.Count == 0)
    {
        Console.WriteLine("  該当なし(申請書の用意が必要な日はありません)");
    }
    else
    {
        foreach (var g in formRows.GroupBy(r => r.FormName).OrderByDescending(g => g.Count()))
            Console.WriteLine($"  {Pad(g.Key, 30)}{g.Count(),5} 件  " +
                              $"({g.Select(r => r.PersonName).Distinct().Count()} 名)");
        Console.WriteLine($"  {Pad("合計", 30)}{formRows.Count,5} 件");

        if (formsOutPath != null)
        {
            ApplicationFormReport.Write(formsOutPath, formRows, result.TargetYear, result.TargetMonth);
            Console.WriteLine();
            Console.WriteLine($"  出力先 : {formsOutPath}");
        }
    }
}

// 一時保存ファイル(Excel)の書き出し。保存・再開の回帰確認に使う。
if (draftExcelOut != null)
{
    Console.WriteLine();
    Console.WriteLine("================ 一時保存ファイル(Excel) ================");
    var sheet = ReportSheetBuilder.Build(result, templatePath);

    // 画面での編集を1件だけ真似ておく(保留を開き直したときに残ることを確かめる)
    var edited = sheet.Employees.FirstOrDefault(e => e.Shift.Any(v => v.Length > 0));
    int editedDay = -1;
    if (edited != null)
    {
        editedDay = Array.FindIndex(edited.Shift, v => v.Length > 0);
        edited.Shift[editedDay] = "有";
        edited.ShiftEdited[editedDay] = true;
        edited.Note[editedDay] = "回帰確認用の備考";
    }

    DraftExcelFile.Save(draftExcelOut, sheet, new DraftInputs
    {
        ShiftPath = shiftPath,
        ShiftSheetName = shiftSheet,
        PunchPath = punchPath,
        PunchSheetName = punchSheet,
        TemplatePath = templatePath ?? "",
        MastersDirectory = mastersDir,
        AutoDetectYearMonth = true,
        OnlyPersonsInShift = options.OnlyPersonsInShift
    });
    Console.WriteLine($"  出力先   : {draftExcelOut}");
    Console.WriteLine($"  社員     : {sheet.Employees.Count} 名 / {sheet.DayCount} 日");
    Console.WriteLine($"  対象年月 : {DraftExcelFile.PeekPeriod(draftExcelOut)}");
    if (File.Exists(draftExcelOut + ".bak"))
        Console.WriteLine($"  控え     : {draftExcelOut}.bak (上書き前の内容)");

    var reopened = DraftExcelFile.Load(draftExcelOut, null);
    Console.WriteLine($"  読み直し : 社員 {reopened.Sheet.Employees.Count} 名 / 修正履歴 {reopened.Sheet.History.Count} 件");
    foreach (var m in reopened.Messages) Console.WriteLine($"    {m}");

    // 画面の「保留を開く」と同じ手順: 突合し直した帳票に、保留していた編集を重ねる
    var rebuilt = ReportSheetBuilder.Build(result, templatePath);
    var merged = rebuilt.ApplyDraftEdits(reopened.Sheet, null);
    Console.WriteLine($"  重ね直し : {merged.Describe()}");
    if (merged.MissingEmployees.Count > 0)
        Console.WriteLine($"    取り込めなかった社員 : {string.Join(", ", merged.MissingEmployees)}");
    if (edited != null && editedDay >= 0)
    {
        var after = rebuilt.Employees.FirstOrDefault(e => e.Key == edited.Key);
        var ok = after != null && after.Shift[editedDay] == "有" && after.ShiftEdited[editedDay]
                 && after.Note[editedDay] == "回帰確認用の備考";
        Console.WriteLine($"    {edited.Name} {editedDay + 1}日 の編集 : {(ok ? "残っています" : "失われました")}");
        if (!ok) return 1;
    }
}

// 帳票の一括出力(仕様書 第16章)
if (outDir != null)
{
    Directory.CreateDirectory(outDir);
    var stamp = $"{result.TargetYear}{result.TargetMonth:00}";
    var sheet = ReportSheetBuilder.Build(result, templatePath);
    var outputs = new List<ReportOutputResult>();

    Console.WriteLine();
    Console.WriteLine("================ 帳票出力 ================");

    if (bookTemplate != null)
        outputs.Add(AttendanceBookWriter.Write(sheet, bookTemplate, Path.Combine(outDir, $"出勤簿_{stamp}.xls")));

    if (payrollTemplate != null && result.Masters != null)
        outputs.Add(PartTimePayrollWriter.Write(result, payrollTemplate,
            Path.Combine(outDir, $"パートアルバイト給与計算表_{stamp}.xlsx"), result.Masters.BreakRules));

    // 参考出力。画面には出さず、--out-dir を指定したときだけ作る
    outputs.Add(StandardReports.WriteDailySummary(result, Path.Combine(outDir, $"日別集計表_{stamp}.xlsx")));
    outputs.Add(StandardReports.WriteEditHistory(sheet, Path.Combine(outDir, $"修正履歴一覧_{stamp}.xlsx")));
    outputs.Add(StandardReports.WritePunchDetail(result, Path.Combine(outDir, $"打刻詳細一覧_{stamp}.xlsx")));

    foreach (var o in outputs)
    {
        Console.WriteLine($"  {(o.Success ? "OK" : "NG")}  {o.Summary}");
        foreach (var m in o.Messages) Console.WriteLine($"        {m}");
    }

    // 実行ログ(仕様書 第18.2章)
    var logInputs = new List<LogInputFile>
    {
        LogInputFile.From("シフト表", shiftPath, shiftSheet ?? ""),
        LogInputFile.From("打刻データ", punchPath, punchSheet ?? "")
    };
    foreach (var extra in extraPunchPaths) logInputs.Add(LogInputFile.From("打刻データ(追加)", extra));
    if (templatePath != null) logInputs.Add(LogInputFile.From("帳票テンプレート", templatePath));

    var logPath = ExecutionLog.Write(Path.Combine(outDir, "ログ"), result, logInputs,
                                     outputs.Where(o => o.Success).Select(o => o.Path).ToList(),
                                     sheet.History.Entries);
    Console.WriteLine($"  実行ログ : {logPath}");
}

return 0;

// 全角文字を考慮した簡易パディング(表示用)
static string Pad(string s, int width)
{
    int w = s.Sum(ch => ch < 0x100 ? 1 : 2);
    return s + new string(' ', Math.Max(1, width - w));
}
