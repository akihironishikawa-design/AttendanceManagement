using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Matching;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Parsing;

namespace TakaneAttendance.Core.Reporting;

/// <summary>1人1日分の判定結果(画面のセルに出す分)。</summary>
/// <param name="Judgement">主判定。4行の背景色に使う(仕様書 v3.0 第15.1章)。</param>
/// <param name="Label">判定結果行に出す文言(要確認 / 打刻漏れ / 遅 / 早退 / 早出 / 時間外 / -)。</param>
/// <param name="Note">判定の内訳。ツールチップ・詳細表示に使う。</param>
public readonly record struct CellJudgement(Judgement Judgement, string Label, string Note)
{
    public static readonly CellJudgement None = new(Models.Judgement.Normal, "-", "");

    /// <summary>日付セル4行に塗る背景色(RRGGBB)。</summary>
    public string ColorHex => JudgementInfo.ColorHex(Judgement);

    /// <summary>正常・対象外以外か(画面で目立たせる対象)。</summary>
    public bool NeedsAttention => Judgement is not (Models.Judgement.Normal or Models.Judgement.Excluded);
}

/// <summary>
/// 出席記録レポート画面のセル1つ分を判定し直す。
///
/// 画面でシフト・打刻を書き換えたときに、その場で矛盾の有無を出し直すために使う。
/// 判定そのものは突合時と同じ <see cref="RuleEngine"/> を通すため、
/// 突合直後の判定と画面編集後の判定が食い違わない。
/// </summary>
public sealed class ReportJudge
{
    private readonly ShiftTypeMaster _shiftTypes;
    private readonly RuleEngine _rules;

    public ReportJudge(MasterSet masters, MatchingOptions options)
    {
        _shiftTypes = masters.ShiftTypes;
        _rules = new RuleEngine(masters, options);
    }

    /// <param name="shiftValue">シフト行のセル値(勤務区分の文字値、または予定開始時刻)</param>
    /// <param name="punchValue">打刻行のセル値(タイムレコーダーの原文)</param>
    /// <param name="plannedEndValue">画面で指定された予定終了時刻。空なら雇用区分・マスタから補完する。</param>
    public CellJudgement Evaluate(
        PersonRef person, DateOnly date, string shiftValue, string punchValue, string plannedEndValue = "")
    {
        var shift = BuildShift(person, date, shiftValue, plannedEndValue);
        var punch = BuildPunch(person, date, punchValue);

        // シフトも打刻も無い日は、そもそも突合の対象にならない(判定しない)
        if (shift == null && punch == null) return CellJudgement.None;

        var daily = new AttendanceDaily
        {
            Person = person,
            WorkDate = date,
            Shift = shift,
            Punch = punch,
            MatchStatus = !person.IsResolved ? MatchStatus.Unresolved
                        : shift != null && punch != null ? MatchStatus.Both
                        : shift != null ? MatchStatus.ShiftOnly
                        : MatchStatus.PunchOnly
        };

        _rules.Evaluate(daily);
        return new CellJudgement(daily.Judgement, daily.JudgementLabel, daily.ResultText);
    }

    private ShiftDaily? BuildShift(PersonRef person, DateOnly date, string value, string plannedEndValue)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return null;

        // 画面で予定終了を指定した場合はそれを優先する(判定エンジン側の補完より先)
        TimeSpan? plannedEnd = null;
        var endTimes = TimeText.ExtractTimes(plannedEndValue ?? "");
        if (endTimes.Count > 0) plannedEnd = endTimes[0];

        // 時刻セル = 通常勤務の予定開始時刻。それ以外は勤務区分の文字値。
        var times = TimeText.ExtractTimes(text);
        if (times.Count > 0)
        {
            return new ShiftDaily
            {
                Person = person,
                WorkDate = date,
                RawValue = text,
                Kind = ShiftKind.Work,
                PlannedStart = times[0],
                PlannedEnd = plannedEnd,
                SourceCell = "画面"
            };
        }

        return new ShiftDaily
        {
            Person = person,
            WorkDate = date,
            RawValue = text,
            Kind = _shiftTypes.Resolve(text),
            ShiftTypeCode = text,
            SourceCell = "画面"
        };
    }

    private static PunchDaily? BuildPunch(PersonRef person, DateOnly date, string value)
    {
        var text = (value ?? "").Trim();
        if (text.Length == 0) return null;

        return new PunchDaily
        {
            Person = person,
            WorkDate = date,
            RawValue = text,
            Times = TimeText.ExtractTimes(text),
            SourceCell = "画面"
        };
    }
}
