using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Matching;

/// <summary>
/// シフトと打刻を「正規化社員名 + 日付」で突合する。
/// 詳細設計書 v2.0 第9章のアルゴリズムに準拠。社員番号は突合キーに使わない。
/// </summary>
public sealed class AttendanceMatcher
{
    public IReadOnlyList<AttendanceDaily> Match(
        IEnumerable<ShiftDaily> shifts,
        IEnumerable<PunchDaily> punches,
        MatchingOptions options)
    {
        var shiftMap = new Dictionary<(string, DateOnly), ShiftDaily>();
        foreach (var s in shifts)
        {
            var key = (s.Person.Key, s.WorkDate);
            // 同一キーの重複は後勝ちにせず、先に読んだ行を残して警告対象にする
            if (!shiftMap.ContainsKey(key)) shiftMap[key] = s;
        }

        var punchMap = new Dictionary<(string, DateOnly), PunchDaily>();
        foreach (var p in punches)
        {
            var key = (p.Person.Key, p.WorkDate);
            if (!punchMap.ContainsKey(key)) punchMap[key] = p;
        }

        // 社員ごとの代表情報を先に決めておく(同一社員の全明細で同じ値を表示するため)。
        //   部門     : シフト表を優先する。締めの単位はシフト表の部門(競技課・営業課)のため。
        //   作業番号 : 打刻データにしかないため打刻側を採用する。
        var personByKey = new Dictionary<string, PersonRef>();
        foreach (var s in shifts) personByKey.TryAdd(s.Person.Key, s.Person);
        foreach (var p in punches)
        {
            if (!personByKey.TryGetValue(p.Person.Key, out var existing))
            {
                personByKey[p.Person.Key] = p.Person;
                continue;
            }
            personByKey[p.Person.Key] = new PersonRef
            {
                SourceName = existing.SourceName,
                NormalizedName = existing.NormalizedName,
                CanonicalName = existing.CanonicalName ?? p.Person.CanonicalName,
                Key = existing.Key,
                Department = Prefer(existing.Department, p.Person.Department),
                EmployeeNo = Prefer(p.Person.EmployeeNo, existing.EmployeeNo),
                // 雇用区分は従業員マスタで解決済みのため、どちらの側でも同じ値になる
                Employment = existing.Employment
            };
        }

        static string? Prefer(string? primary, string? fallback)
            => string.IsNullOrWhiteSpace(primary) ? fallback : primary;

        var keys = new HashSet<(string, DateOnly)>(shiftMap.Keys);
        keys.UnionWith(punchMap.Keys);

        var results = new List<AttendanceDaily>(keys.Count);
        foreach (var key in keys)
        {
            shiftMap.TryGetValue(key, out var shift);
            punchMap.TryGetValue(key, out var punch);

            // シフトのみの社員を除外するオプション(シフト表が一部門のみの場合に使う)
            if (options.OnlyPersonsInShift && shift == null) continue;
            if (options.OnlyPersonsInPunch && punch == null) continue;

            var person = personByKey.TryGetValue(key.Item1, out var pr)
                ? pr
                : (shift?.Person ?? punch!.Person);

            // 仕様書 v3.0 第11.3章。氏名を解決できない行は自動突合せず要確認とする。
            var status = !person.IsResolved ? MatchStatus.Unresolved
                       : shift != null && punch != null ? MatchStatus.Both
                       : shift != null ? MatchStatus.ShiftOnly
                       : MatchStatus.PunchOnly;

            results.Add(new AttendanceDaily
            {
                Person = person,
                WorkDate = key.Item2,
                Shift = shift,
                Punch = punch,
                MatchStatus = status
            });
        }

        return results
            .OrderBy(r => r.Person.Department)
            .ThenBy(r => r.PersonName)
            .ThenBy(r => r.WorkDate)
            .ToList();
    }
}

/// <summary>
/// 突合・判定の動作設定。
/// しきい値は判定閾値マスタ(judgement_rule.xml)から読み込む(仕様書 v3.0 第17章)。
/// </summary>
public sealed class MatchingOptions
{
    /// <summary>シフト表に載っている社員のみを対象にする(シフト表が一部門だけの場合に有効)</summary>
    public bool OnlyPersonsInShift { get; set; }
    /// <summary>打刻データに載っている社員のみを対象にする</summary>
    public bool OnlyPersonsInPunch { get; set; }

    /// <summary>早出のしきい値(分)。予定開始の N 分以上前の出勤を早出とする。29分は対象外・30分は対象。</summary>
    public int EarlyInMinutes { get; set; } = 30;
    /// <summary>時間外のしきい値(分)。予定終了の N 分以上後の退勤を時間外とする。</summary>
    public int OvertimeMinutes { get; set; } = 30;
    /// <summary>遅刻・早退の許容(分)。仕様書 v3.0 第13.2章により1分超過から遅刻のため 0。</summary>
    public int ToleranceMinutes { get; set; }
    /// <summary>正社員の拘束時間(分)。予定終了 = 予定開始 + この時間。既定は9時間30分。</summary>
    public int FullTimeSpanMinutes { get; set; } = 570;
}

/// <summary>
/// 判定ルールの評価。統合仕様書 v3.0 第13章「勤怠判定仕様」に準拠する。
///
/// 評価の順序は仕様書 13.1 のとおり、
///   勤務区分 → 打刻件数 → 遅刻・早退・早出・時間外
/// とする。1日に複数の判定コードを保持し、画面に出す主判定は
/// <see cref="AttendanceDaily.Judgement"/> の優先順位で1つに絞る。
/// </summary>
public sealed class RuleEngine
{
    private readonly WorkingHoursMaster _workingHours;
    private readonly EmployeeMaster _employees;
    private readonly HolidayMaster _holidays;
    private readonly BreakRuleMaster _breakRule;
    private readonly MatchingOptions _options;

    public RuleEngine(MasterSet masters, MatchingOptions options)
    {
        _workingHours = masters.WorkingHours;
        _employees = masters.Employees;
        _holidays = masters.Holidays;
        _breakRule = masters.BreakRules;
        _options = options;
    }

    public void Evaluate(AttendanceDaily a)
    {
        var codes = a.ResultCodes;
        codes.Clear();
        a.WorkTime = null;

        // ---- 1. 社員名不一致(仕様書 13.2「社員名不一致」) ----
        if (!a.Person.IsResolved)
        {
            codes.Add(ResultCode.NameUnresolved);
            return;   // 突合の信頼性がないため、以降の判定は行わない
        }

        var shift = a.Shift;
        var punch = a.Punch;
        int punchCount = punch?.PunchCount ?? 0;

        // ---- 2. シフトなし＋打刻(仕様書 13.2「シフトなし＋打刻」) ----
        if (shift == null)
        {
            codes.Add(punchCount > 0 ? ResultCode.NoShiftPunch : ResultCode.DataError);
            return;
        }

        // ---- 3. 勤務区分による判定。通常勤務以外はここで確定する ----
        switch (shift.Kind)
        {
            case ShiftKind.Excluded:
                codes.Add(ResultCode.Excluded);
                return;

            case ShiftKind.DayOff:
                codes.Add(punchCount > 0 ? ResultCode.DayOffPunch : ResultCode.DayOff);
                return;

            case ShiftKind.PaidLeave:
                codes.Add(punchCount > 0 ? ResultCode.PaidLeavePunch : ResultCode.PaidLeave);
                return;

            case ShiftKind.BusinessTrip:
                // 出張は打刻件数で終日／半日を分ける(仕様書 13.2)。
                //   0件 = 終日出張(正常。申請書の確認メッセージを出す)
                //   1件 = 打刻漏れ(半日出張候補)
                //   2件 = 半日出張(正常。遅刻・早退は判定しない)
                //   3件以上 = 要確認(半日出張候補＋複数打刻)
                codes.Add(punchCount switch
                {
                    0 => ResultCode.BusinessTripFull,
                    1 => ResultCode.NoPunch,
                    2 => ResultCode.BusinessTripHalf,
                    _ => ResultCode.MultiPunch
                });
                if (punchCount >= 2) a.WorkTime = CalculateWorkTime(punch);
                return;

            case ShiftKind.Other:
            case ShiftKind.Unknown:
                // マスタ未登録の文字値も「その他」として扱う(内訳は要確認 Q-04)
                codes.Add(punchCount > 0 ? ResultCode.OtherPunch : ResultCode.Other);
                return;

            case ShiftKind.Work:
                break;
        }

        // ---- 4. 予定終了時刻の確定 ----
        // 打刻の有無によらず先に決める。打刻漏れの日でも、申請書や帳票に
        // 「シフト上の退社時間」を出せるようにするため(仕様書 8.3・14.4)。
        shift.PlannedEnd = ResolvePlannedEnd(a.Person, shift);

        // ---- 5. 通常勤務の打刻件数(仕様書 第12章) ----

        // 0件・1件はどちらも打刻漏れ。1件のときに出勤・退勤の区別はしない。
        if (punchCount <= 1)
        {
            codes.Add(ResultCode.NoPunch);
            return;
        }

        // 3件以上は要確認。中間打刻は計算から除外し、最初と最後で時刻判定を続ける。
        if (punchCount >= 3) codes.Add(ResultCode.MultiPunch);

        var firstPunch = punch!.ActualIn!.Value;
        var lastPunch = punch.ActualOut!.Value;

        // 勤務時間(15分丸め → 拘束時間 → 休憩)。仕様書 第14章。
        // 遅刻・早退の判定には丸めた値を使わない(仕様書 14.1)。
        a.WorkTime = CalculateWorkTime(punch);

        // ---- 6. 出勤側(遅刻・早出) ----
        if (shift.PlannedStart is { } plannedStart)
        {
            var diff = (int)(firstPunch - plannedStart).TotalMinutes;
            // 遅刻は1分超過から対象
            if (diff > _options.ToleranceMinutes) codes.Add(ResultCode.Late);
            // 早出は「予定開始 − 30分」以前。29分は対象外、30分は対象。
            else if (-diff >= _options.EarlyInMinutes) codes.Add(ResultCode.EarlyIn30);
        }

        // ---- 7. 退勤側(早退・時間外) ----
        // 予定終了が確定しない場合(パート等で未登録)は判定しない。
        if (shift.PlannedEnd is { } plannedEnd)
        {
            var diff = (int)(lastPunch - plannedEnd).TotalMinutes;
            if (diff >= _options.OvertimeMinutes) codes.Add(ResultCode.Overtime30);
            // 早退は必要退勤時刻に満たない場合。遅刻しても基準は後ろ倒ししない(仕様書 13.1)。
            else if (-diff > _options.ToleranceMinutes) codes.Add(ResultCode.EarlyLeave);
        }

        if (codes.Count == 0) codes.Add(ResultCode.Normal);
    }

    private WorkTime? CalculateWorkTime(PunchDaily? punch)
        => punch?.ActualIn is { } inTime && punch.ActualOut is { } outTime
            ? _breakRule.Calculate(inTime, outTime)
            : null;

    /// <summary>
    /// 予定終了時刻を確定する(仕様書 8.3・14.4)。
    ///
    ///   1. シフト表の値、または画面で修正された値
    ///   2. 正社員 … 予定開始 + 拘束9時間30分(早退の基準式)
    ///   3. パート・アルバイト … 勤務パターンマスタ
    ///
    /// 正社員は 14.4 の基準式が優先されるため、勤務パターンマスタより先に適用する。
    /// </summary>
    private TimeSpan? ResolvePlannedEnd(PersonRef person, ShiftDaily shift)
    {
        if (shift.PlannedEnd is { } given) return given;
        if (shift.PlannedStart is not { } start) return null;

        // パート・アルバイトは従業員マスタの「1日の拘束時間」が最優先
        if (person.Employment != EmploymentType.FullTime &&
            _employees.WorkHoursOf(person.Key) is { } contracted)
            return start + contracted;

        // 所属部と時期で所定労働時間が変わる場合(年間カレンダーの【部門別所定労働時間】)。
        // 引き当ては従業員マスタの所属部で行う。シフト表や打刻データの部門ではない。
        var division = _employees.DivisionOf(person.Key);
        var span = _workingHours.SpanMinutesOf(division, shift.WorkDate,
                                               _holidays.IsWeekendOrHoliday(shift.WorkDate));
        if (span is { } minutes) return start + TimeSpan.FromMinutes(minutes);

        // 所定労働時間マスタに無い所属部は、これまでどおり拘束時間で決める(仕様書 14.4)
        return person.Employment == EmploymentType.FullTime
            ? start + TimeSpan.FromMinutes(_options.FullTimeSpanMinutes)
            : null;
    }
}
