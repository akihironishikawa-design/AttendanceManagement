using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Matching;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Naming;

namespace TakaneAttendance.Cli;

/// <summary>
/// 統合仕様書 v3.0 の受入条件(第21章)と受入テスト主要ケース(付録B)を、
/// 実装した判定エンジンにそのまま通して確認する。
///
/// マスタの設定値もそのまま使うため、masters\ を書き換えたときの影響も分かる。
/// 単体テストのプロジェクトを増やす前の、最低限の回帰確認として用意している。
/// </summary>
internal static class AcceptanceTests
{
    private sealed record Case(
        string Id, string Title, string Shift, string[] Punches,
        string ExpectedLabel, ResultCode? ExpectedCode = null,
        EmploymentType Employment = EmploymentType.FullTime);

    // 付録B の主要ケース。予定シフトはすべて 08:00(正社員 → 正常退勤下限 17:30)。
    private static readonly Case[] Cases =
    {
        new("T-001", "通常",             "8:00", new[] { "08:00", "17:30" }, "-",       ResultCode.Normal),
        new("T-002", "遅刻",             "8:00", new[] { "08:01", "17:30" }, "遅",      ResultCode.Late),
        new("T-003", "早退境界前",       "8:00", new[] { "08:00", "17:29" }, "早",     ResultCode.EarlyLeave),
        new("T-004", "早退境界",         "8:00", new[] { "08:00", "17:30" }, "-",       ResultCode.Normal),
        new("T-005", "早出29分",         "8:00", new[] { "07:31", "17:30" }, "-",       ResultCode.Normal),
        new("T-006", "早出30分",         "8:00", new[] { "07:30", "17:30" }, "早出",    ResultCode.EarlyIn30),
        new("T-007", "0打刻",            "8:00", Array.Empty<string>(),      "打刻漏れ", ResultCode.NoPunch),
        new("T-008", "1打刻",            "8:00", new[] { "08:00" },          "打刻漏れ", ResultCode.NoPunch),
        new("T-009", "3打刻",            "8:00", new[] { "07:55", "12:00", "17:30" }, "要確認", ResultCode.MultiPunch),
        new("T-010", "公休",             "公",   Array.Empty<string>(),      "-",       ResultCode.DayOff),
        new("T-011", "公休打刻",         "公",   new[] { "08:00", "17:30" }, "要確認",  ResultCode.DayOffPunch),
        new("T-012", "有給打刻",         "有",   new[] { "08:00", "17:30" }, "要確認",  ResultCode.PaidLeavePunch),
        new("T-013", "終日出張",         "出張", Array.Empty<string>(),      "-",       ResultCode.BusinessTripFull),
        new("T-014", "半日出張",         "出張", new[] { "08:00", "12:00" }, "-",       ResultCode.BusinessTripHalf),
    };

    // 第21章 受入条件のうち、遅刻＋早退の同時成立(第14.4章の表)。
    private static readonly Case[] CombinedCases =
    {
        new("14.4-a", "遅刻＋早退(主表示は遅刻)", "8:00", new[] { "08:20", "17:29" }, "遅", ResultCode.Late),
        new("14.4-b", "遅刻のみ",                 "8:00", new[] { "08:20", "17:30" }, "遅", ResultCode.Late),
    };

    // 第14.3章の休憩境界。丸め後拘束時間 → 休憩時間。
    private static readonly (string Id, string Title, double SpanHours, int ExpectedBreak)[] BreakCases =
    {
        ("T-015", "休憩6時間",   6.00, 15),
        ("T-016", "休憩6時間超", 6.25, 45),
        ("T-017", "休憩8時間",   8.00, 45),
        ("T-018", "休憩8時間超", 8.25, 90),
    };

    public static int Run(string? mastersDirectory)
    {
        var dir = mastersDirectory ?? MasterSet.DefaultDirectory;
        var masters = MasterSet.Load(dir);
        foreach (var m in masters.Messages) Console.WriteLine("  " + m);

        var options = new MatchingOptions();
        masters.JudgementRules.ApplyTo(options);

        Console.WriteLine($"マスタ     : {dir}");
        Console.WriteLine($"判定閾値   : {masters.JudgementRules.Summary}");
        Console.WriteLine($"休憩ルール : {masters.BreakRules.Summary}");
        Console.WriteLine();

        int failed = 0;
        Console.WriteLine("================ 付録B 受入テスト主要ケース ================");
        Console.WriteLine($"{"ID",-8}{"ケース",-24}{"シフト",-8}{"打刻",-24}{"期待",-10}{"結果",-10}{"判定"}");
        foreach (var c in Cases.Concat(CombinedCases))
            failed += RunCase(c, masters, options) ? 0 : 1;

        Console.WriteLine();
        Console.WriteLine("================ 第14.3章 休憩境界 ================");
        Console.WriteLine($"{"ID",-8}{"ケース",-16}{"拘束",-10}{"期待",-8}{"結果",-8}{"判定"}");
        foreach (var (id, title, spanHours, expected) in BreakCases)
        {
            int actual = masters.BreakRules.ResolveBreakMinutes(spanHours);
            bool ok = actual == expected;
            if (!ok) failed++;
            Console.WriteLine($"{id,-8}{Pad(title, 16)}{Hm(spanHours),-10}{expected + "分",-8}{actual + "分",-8}{(ok ? "OK" : "NG")}");
        }

        Console.WriteLine();
        Console.WriteLine(failed == 0
            ? $"すべて仕様どおりです ({Cases.Length + CombinedCases.Length + BreakCases.Length} 件)"
            : $"仕様と異なる結果が {failed} 件あります");
        return failed == 0 ? 0 : 1;
    }

    private static bool RunCase(Case c, MasterSet masters, MatchingOptions options)
    {
        var engine = new RuleEngine(masters, options);
        var person = new PersonRef
        {
            SourceName = "受入 太郎",
            NormalizedName = "受入太郎",
            CanonicalName = "受入 太郎",
            Key = NameNormalizer.Normalize("受入 太郎"),
            Employment = c.Employment
        };

        var date = new DateOnly(2026, 9, 1);
        var daily = new AttendanceDaily
        {
            Person = person,
            WorkDate = date,
            Shift = BuildShift(person, date, c.Shift, masters.ShiftTypes),
            Punch = c.Punches.Length == 0 ? null : new PunchDaily
            {
                Person = person,
                WorkDate = date,
                RawValue = string.Concat(c.Punches),
                Times = c.Punches.Select(TimeSpan.Parse).ToArray(),
                SourceCell = "受入テスト"
            },
            MatchStatus = c.Punches.Length == 0 ? MatchStatus.ShiftOnly : MatchStatus.Both
        };

        engine.Evaluate(daily);

        bool ok = daily.JudgementLabel == c.ExpectedLabel
               && (c.ExpectedCode is not { } code || daily.ResultCodes.Contains(code));
        if (!ok) return Report(c, daily, false);
        return Report(c, daily, true);
    }

    private static bool Report(Case c, AttendanceDaily daily, bool ok)
    {
        var punches = c.Punches.Length == 0 ? "(なし)" : string.Join(" ", c.Punches);
        var codes = string.Join(",", daily.ResultCodes.Select(ResultCodeInfo.CodeName));
        Console.WriteLine($"{c.Id,-8}{Pad(c.Title, 24)}{Pad(c.Shift, 8)}{Pad(punches, 24)}" +
                          $"{Pad(c.ExpectedLabel, 10)}{Pad(daily.JudgementLabel, 10)}" +
                          $"{(ok ? "OK" : "NG")}  {codes}");
        return ok;
    }

    private static ShiftDaily BuildShift(PersonRef person, DateOnly date, string value, ShiftTypeMaster shiftTypes)
    {
        if (TimeSpan.TryParse(value, out var start))
            return new ShiftDaily
            {
                Person = person, WorkDate = date, RawValue = value,
                Kind = ShiftKind.Work, PlannedStart = start, SourceCell = "受入テスト"
            };

        return new ShiftDaily
        {
            Person = person, WorkDate = date, RawValue = value,
            Kind = shiftTypes.Resolve(value), ShiftTypeCode = value, SourceCell = "受入テスト"
        };
    }

    private static string Hm(double hours) => $"{(int)hours}:{(int)Math.Round((hours % 1) * 60):00}";

    /// <summary>全角を2文字ぶんとして数え、列をそろえる。</summary>
    private static string Pad(string value, int width)
    {
        int visible = value.Sum(ch => ch < 0x80 ? 1 : 2);
        return value + new string(' ', Math.Max(1, width - visible));
    }
}
