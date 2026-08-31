using TakaneAttendance.Core.Masters;

namespace TakaneAttendance.Cli;

/// <summary>
/// マスタ編集画面の読み書きを、画面を開かずに確かめる。
///
/// masters フォルダを作業用の場所へ複写し、画面と同じ経路(MasterEditor)で
/// 「読む → 書く → もう一度読む」を行って、内容が変わらないことを確認する。
/// 8マスタすべてを画面から編集できるようにしたため、
/// 保存でファイルを壊していないかを機械的に確かめられるようにしておく。
/// </summary>
internal static class MasterRoundTrip
{
    public static int Run(string mastersDir, string workDir)
    {
        if (!Directory.Exists(mastersDir))
        {
            Console.Error.WriteLine($"マスタのフォルダがありません: {mastersDir}");
            return 1;
        }

        Directory.CreateDirectory(workDir);
        foreach (var file in Directory.GetFiles(mastersDir, "*.xml"))
            File.Copy(file, Path.Combine(workDir, Path.GetFileName(file)), overwrite: true);

        Console.WriteLine($"作業用に複写 : {workDir}");
        Console.WriteLine();

        var before = Read(workDir, out var messages);
        foreach (var m in messages) Console.WriteLine("  [読み込み] " + m);

        Write(workDir, before);

        var after = Read(workDir, out var messages2);
        foreach (var m in messages2) Console.WriteLine("  [読み直し] " + m);

        Console.WriteLine();
        Console.WriteLine("================ 読む → 書く → 読む ================");

        int failed = 0;
        foreach (var key in before.Keys)
        {
            var a = before[key];
            var b = after.TryGetValue(key, out var v) ? v : new List<string>();
            bool same = a.SequenceEqual(b);
            Console.WriteLine($"  {(same ? "OK  " : "NG  ")}{key,-28} {a.Count,4} 件");
            if (same) continue;

            failed++;
            foreach (var line in a.Except(b).Take(5)) Console.WriteLine($"        書く前のみ : {line}");
            foreach (var line in b.Except(a).Take(5)) Console.WriteLine($"        書いた後のみ: {line}");
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? "すべてのマスタで、保存しても内容が変わりませんでした。"
            : $"{failed} 個のマスタで内容が変わりました。");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>マスタごとの中身を、比較しやすい文字列の一覧にして返す。</summary>
    private static Dictionary<string, List<string>> Read(string dir, out List<string> messages)
    {
        messages = new List<string>();
        string P(string name) => Path.Combine(dir, name);
        var result = new Dictionary<string, List<string>>();

        result["社員名 別名"] = MasterEditor.LoadAliases(P(MasterSet.AliasFileName), messages)
            .Select(a => $"{a.Source}|{a.Canonical}|{a.Note}").ToList();

        var (divisions, periods) = MasterEditor.LoadWorkingHours(P(MasterSet.WorkingHoursFileName), messages);
        result["所定労働時間(所属部)"] = divisions.Select(d => $"{d.Name}|{d.WeekdayBreak}|{d.HolidayBreak}").ToList();
        result["所定労働時間(期間)"] = periods.Select(p => $"{p.Division}|{p.From}|{p.To}|{p.Weekday}|{p.Holiday}").ToList();

        result["従業員"] = MasterEditor.LoadEmployees(P(MasterSet.EmployeeFileName), messages)
            .Select(e => $"{e.No}|{e.Name}|{e.Division}|{e.Department}|{e.Employment}|{e.Pattern}|" +
                         $"{e.WorkHours}|{e.HourlyWage}|{e.Joined}|{e.Left}").ToList();

        result["祝日"] = MasterEditor.LoadHolidays(P(MasterSet.HolidayFileName), messages)
            .Select(d => $"{d.Date}|{d.Kind}|{d.Note}").ToList();

        result["申請書の対応"] = MasterEditor.LoadApplicationForms(P(MasterSet.ApplicationFormFileName), messages)
            .Select(f => $"{f.Code}|{f.FormName}|{f.Reason}").ToList();

        var bands = MasterEditor.LoadBreakRule(P(MasterSet.BreakRuleFileName), messages, out var breakSettings);
        result["休憩ルール"] = bands.Select(b => $"{b.UpToHours}|{b.BreakMinutes}").ToList();
        result["休憩ルール"].Add($"(丸め){breakSettings.UnitMinutes}|{breakSettings.InRounding}|{breakSettings.OutRounding}");

        var judgement = MasterEditor.LoadJudgementRule(P(MasterSet.JudgementRuleFileName), messages);
        result["判定閾値"] = new List<string>
        {
            $"{judgement.EarlyInMinutes}|{judgement.OvertimeMinutes}|{judgement.FullTimeSpanMinutes}|{judgement.ToleranceMinutes}"
        };

        // 勤務区分は既定値と合わせた一覧を画面に出しているため、同じ経路で読む
        result["勤務区分"] = ShiftTypeMaster.Load(P(MasterSet.ShiftTypeFileName)).All
            .Select(s => $"{s.Code}|{s.Kind}|{s.Description}").ToList();

        return result;
    }

    /// <summary>画面の「保存」と同じ順序・同じ経路で書き戻す。</summary>
    private static void Write(string dir, Dictionary<string, List<string>> _)
    {
        string P(string name) => Path.Combine(dir, name);
        var messages = new List<string>();

        MasterEditor.SaveAliases(P(MasterSet.AliasFileName),
            MasterEditor.LoadAliases(P(MasterSet.AliasFileName), messages));

        MasterEditor.SaveShiftTypes(P(MasterSet.ShiftTypeFileName),
            ShiftTypeMaster.Load(P(MasterSet.ShiftTypeFileName)).All);

        var (divisions, periods) = MasterEditor.LoadWorkingHours(P(MasterSet.WorkingHoursFileName), messages);
        MasterEditor.SaveWorkingHours(P(MasterSet.WorkingHoursFileName), divisions, periods);

        MasterEditor.SaveEmployees(P(MasterSet.EmployeeFileName),
            MasterEditor.LoadEmployees(P(MasterSet.EmployeeFileName), messages));

        MasterEditor.SaveHolidays(P(MasterSet.HolidayFileName),
            MasterEditor.LoadHolidays(P(MasterSet.HolidayFileName), messages));

        MasterEditor.SaveApplicationForms(P(MasterSet.ApplicationFormFileName),
            MasterEditor.LoadApplicationForms(P(MasterSet.ApplicationFormFileName), messages));

        var bands = MasterEditor.LoadBreakRule(P(MasterSet.BreakRuleFileName), messages, out var breakSettings);
        MasterEditor.SaveBreakRule(P(MasterSet.BreakRuleFileName), bands, breakSettings);

        MasterEditor.SaveJudgementRule(P(MasterSet.JudgementRuleFileName),
            MasterEditor.LoadJudgementRule(P(MasterSet.JudgementRuleFileName), messages));
    }
}
