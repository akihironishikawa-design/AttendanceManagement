using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Matching;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Naming;
using TakaneAttendance.Core.Parsing;

namespace TakaneAttendance.Core;

/// <summary>突合処理の入力。</summary>
public sealed class MatchingRequest
{
    public required string ShiftPath { get; init; }
    public string? ShiftSheetName { get; init; }
    public required string PunchPath { get; init; }
    public string? PunchSheetName { get; init; }
    /// <summary>
    /// 2ファイル目以降の打刻データ(複数拠点分)。仕様書 v3.0 第10.3章。
    /// 完全に一致する重複打刻は統合時に取り除く。
    /// </summary>
    public IReadOnlyList<string> AdditionalPunchPaths { get; init; } = Array.Empty<string>();
    /// <summary>対象年月を明示指定する場合。null ならファイルから判定する。</summary>
    public (int Year, int Month)? TargetYearMonth { get; init; }
    public string? MastersDirectory { get; init; }
    public MatchingOptions Options { get; init; } = new();
}

/// <summary>
/// 突合処理の入口。読み込み → 氏名解決 → 突合 → 判定 までを1回の実行として扱う。
///
/// 打刻データを先に読むのは、打刻データ側の氏名(作業番号を持つ)を正式氏名として
/// 登録してから、シフト表の氏名を解決するため。
/// これにより「空白の違いだけ」の表記ゆれは別名マスタなしで自動解決される。
/// </summary>
public sealed class MatchingService
{
    public MatchingResult Execute(MatchingRequest request)
    {
        var result = new MatchingResult
        {
            ExecutionId = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..6]}",
            StartedAt = DateTime.Now
        };

        var masters = MasterSet.Load(request.MastersDirectory ?? MasterSet.DefaultDirectory);
        result.Masters = masters;
        result.Messages.AddRange(masters.Messages);   // マスタXMLの書式エラーは処理ログに残す

        // 判定閾値マスタの値を突合の設定へ反映する(仕様書 v3.0 第17章)
        masters.JudgementRules.ApplyTo(request.Options);

        // 従業員マスタの正式氏名も解決対象にする(打刻データに現れない社員のため)
        foreach (var e in masters.Employees.All) masters.Alias.RegisterCanonical(e.CanonicalName);

        var normalizer = new NameNormalizer(masters.Alias, masters.Employees);

        // ---- 1. 打刻データ(正式氏名の供給元) ----
        // 複数拠点分を続けて読み、完全に一致する重複打刻を統合時に取り除く。
        var punchParser = new PunchParser(normalizer, masters.Alias);
        var punchResult = punchParser.Parse(request.PunchPath, request.PunchSheetName, request.TargetYearMonth);
        result.Messages.AddRange(punchResult.Messages);
        result.Messages.Add($"[打刻] {punchResult.LayoutSummary}");

        var punchSources = new List<IReadOnlyList<PunchDaily>> { punchResult.Punches };
        foreach (var extra in request.AdditionalPunchPaths)
        {
            var more = punchParser.Parse(extra, request.PunchSheetName, request.TargetYearMonth);
            result.Messages.AddRange(more.Messages);
            result.Messages.Add($"[打刻] {more.LayoutSummary}");
            punchSources.Add(more.Punches);
        }

        var merged = PunchMerger.Merge(punchSources);
        result.Messages.AddRange(merged.Messages);
        var punches = merged.Punches;
        result.PunchRecordCount = punches.Count;

        // ---- 2. シフト表 ----
        var ym = request.TargetYearMonth
                 ?? (punchResult.Year > 0 ? (punchResult.Year, punchResult.Month) : null);
        var shiftParser = new ShiftParser(normalizer, masters.ShiftTypes);
        var shiftResult = shiftParser.Parse(request.ShiftPath, request.ShiftSheetName, ym);
        result.Messages.AddRange(shiftResult.Messages);
        result.Messages.Add($"[シフト] {shiftResult.LayoutSummary}");
        result.ShiftRecordCount = shiftResult.Shifts.Count;

        if (shiftResult.UnknownShiftValues.Count > 0)
            result.Messages.Add($"[注意] 勤務区分マスタに未登録の値: {string.Join(", ", shiftResult.UnknownShiftValues)}");

        result.TargetYear = shiftResult.Year > 0 ? shiftResult.Year : punchResult.Year;
        result.TargetMonth = shiftResult.Month > 0 ? shiftResult.Month : punchResult.Month;

        // ---- 3. シフト側の氏名を、打刻側の正式氏名で再解決 ----
        // (打刻データを読んだ後でないと正式氏名が揃わないため、ここでもう一度解決する)
        var reresolvedShifts = shiftResult.Shifts.Select(s =>
        {
            if (s.Person.IsResolved) return s;
            var person = normalizer.Resolve(s.Person.SourceName, s.Person.Department);
            if (!person.IsResolved) return s;
            if (!person.IsResolved) return s;
            return new ShiftDaily
            {
                Person = person,
                WorkDate = s.WorkDate,
                RawValue = s.RawValue,
                Kind = s.Kind,
                ShiftTypeCode = s.ShiftTypeCode,
                PlannedStart = s.PlannedStart,
                PlannedEnd = s.PlannedEnd,
                SourceCell = s.SourceCell
            };
        }).ToList();

        // ---- 3a. 管理区分がオフの方を対象から外す ----
        // 従業員マスタの「管理区分」のチェックを外した方は勤怠管理の対象にしない。
        // 突合結果の一覧にも帳票にも出さないため、突合を始める前にここで落としておく
        // (マスタに登録の無い方は従来どおり対象のまま)。
        var excludedNames = new SortedSet<string>(StringComparer.Ordinal);
        bool IsManaged(PersonRef person)
        {
            if (masters.Employees.IsManaged(person.Key)) return true;
            excludedNames.Add(person.DisplayName);
            return false;
        }

        var targetShifts = reresolvedShifts.Where(s => IsManaged(s.Person)).ToList();
        var targetPunches = punches.Where(p => IsManaged(p.Person)).ToList();

        // ---- 3b. シフト表の社員一覧(記載順) ----
        // 帳票の社員・並び順・部門はここを基準にするため、氏名はシフトと同じ条件で解決し直す。
        foreach (var entry in shiftResult.Roster)
        {
            var person = normalizer.Resolve(entry.SourceName, entry.Department);
            if (!masters.Employees.IsManaged(person.Key)) continue;
            result.ShiftRoster.Add(new ShiftRosterEntry
            {
                Key = person.Key,
                SourceName = entry.SourceName,
                DisplayName = person.DisplayName,
                Department = entry.Department,
                Order = entry.Order,
                SourceRow = entry.SourceRow
            });
        }

        // ---- 3b'. 打刻データの社員一覧(記載順) ----
        // 帳票の並び順・氏名・部門はここを基準にする(お客様の出席記録はタイムレコーダー出力の複製のため)。
        foreach (var entry in punchResult.Roster)
            if (masters.Employees.IsManaged(entry.Key)) result.PunchRoster.Add(entry);

        if (excludedNames.Count > 0)
            result.Messages.Add($"[従業員] 管理区分がオフのため対象外にしました: {excludedNames.Count} 名 " +
                                $"({string.Join(" , ", excludedNames)})");

        // ---- 3c. シフト重複の検査(仕様書 7.2 / E-SHIFT-001) ----
        // 同一社員・同一日付にシフトが2件あると、どちらを予定として扱うか決められない。
        var duplicates = targetShifts
            .GroupBy(sh => (sh.Person.Key, sh.WorkDate))
            .Where(g => g.Count() > 1)
            .ToList();

        if (duplicates.Count > 0)
        {
            foreach (var g in duplicates.Take(20))
            {
                var name = g.First().Person.DisplayName;
                var cells = string.Join(" / ", g.Select(sh => sh.SourceCell));
                result.Add(ErrorCodes.Fatal(ErrorCodes.ShiftDuplicated,
                    $"シフト重複のため自動突合できません。{name} の {g.Key.WorkDate:yyyy/MM/dd} が {g.Count()} 件あります。" +
                    "シフト表を修正して取り込み直してください。", cells));
            }
            if (duplicates.Count > 20)
                result.Add(ErrorCodes.Fatal(ErrorCodes.ShiftDuplicated,
                    $"ほかに {duplicates.Count - 20} 件のシフト重複があります。"));

            result.FinishedAt = DateTime.Now;
            return result;   // 仕様書 19章「処理停止」。突合を開始しない。
        }

        // ---- 4. 突合 ----
        var matcher = new AttendanceMatcher();
        var details = matcher.Match(targetShifts, targetPunches, request.Options);

        // ---- 5. 判定 ----
        result.Messages.Add($"[休憩] {masters.BreakRules.Summary}");
        result.Messages.Add($"[判定閾値] {masters.JudgementRules.Summary}");
        result.Messages.Add($"[所定労働時間] {masters.WorkingHours.Summary}");
        result.Messages.Add($"[パート・アルバイト] {masters.Employees.PartTimeSummary}");
        result.Messages.Add($"[申請書] {masters.ApplicationForms.Summary}");
        result.Messages.Add($"[祝日] {masters.Holidays.Summary}");
        result.Messages.Add(masters.Employees.EntryCount > 0
            ? $"[従業員] マスタ登録 {masters.Employees.EntryCount} 名 / {masters.Employees.ManagedSummary}"
            : "[注意] 従業員マスタ(employee.xml)が空です。全員を正社員として扱い、早退は「予定開始 + " +
              $"{masters.JudgementRules.FullTimeSpanMinutes / 60}時間{masters.JudgementRules.FullTimeSpanMinutes % 60:00}分」で判定します。");
        var engine = new RuleEngine(masters, request.Options);
        foreach (var d in details) engine.Evaluate(d);
        result.Details.AddRange(details);

        // ---- 6. 未解決氏名の集計(別名マスタ整備の材料) ----
        CollectUnresolved(result, targetShifts, targetPunches);

        // ---- 7. 期間カバレッジの確認 ----
        // タイムレコーダーの出力が週単位のことがあるため、シフトの対象日数と食い違う場合に注意喚起する。
        // (対象月の一部しか打刻がないと「両打刻なし」が大量に出るため)
        if (punchResult.DayCount > 0 && shiftResult.DayCount > punchResult.DayCount)
        {
            var punchDays = punches.Select(p => p.WorkDate).Distinct().OrderBy(d => d).ToList();
            if (punchDays.Count > 0)
                result.Add(ErrorCodes.Warning(ErrorCodes.PeriodMismatch,
                    $"打刻データの対象は {punchDays.First():yyyy/MM/dd}〜{punchDays.Last():yyyy/MM/dd} " +
                    $"({punchResult.DayCount}日分)ですが、シフトは{shiftResult.DayCount}日分あります。" +
                    "打刻のない日は「打刻漏れ」として集計されます。月次の締めでは1か月分の打刻データを取り込んでください。"));
        }

        // ---- 8. 検算 ----
        int expectedKeys = targetShifts.Select(s => (s.Person.Key, s.WorkDate))
            .Union(targetPunches.Select(p => (p.Person.Key, p.WorkDate))).Count();
        if (!request.Options.OnlyPersonsInShift && !request.Options.OnlyPersonsInPunch && expectedKeys != details.Count)
            result.Add(ErrorCodes.Warning(ErrorCodes.CountMismatch,
                $"検算が合いません。突合キー数 {expectedKeys} に対して明細 {details.Count} 件です。"));

        result.FinishedAt = DateTime.Now;
        return result;
    }

    private static void CollectUnresolved(MatchingResult result, IEnumerable<ShiftDaily> shifts, IEnumerable<PunchDaily> punches)
    {
        var map = new Dictionary<string, UnresolvedName>();

        void Add(PersonRef p, string origin)
        {
            if (p.IsResolved) return;
            var key = origin + "" + p.NormalizedName;
            if (!map.TryGetValue(key, out var u))
            {
                u = new UnresolvedName
                {
                    SourceName = p.SourceName,
                    NormalizedName = p.NormalizedName,
                    Origin = origin,
                    Department = p.Department,
                    EmployeeNo = p.EmployeeNo
                };
                map[key] = u;
            }
            u.Occurrences++;
        }

        foreach (var s in shifts) Add(s.Person, "シフト表");
        foreach (var p in punches) Add(p.Person, "打刻データ");

        result.UnresolvedNames.AddRange(map.Values.OrderByDescending(u => u.Occurrences));
    }
}
