using TakaneAttendance.Core.Parsing;

namespace TakaneAttendance.Core.Reporting;

/// <summary>
/// 勤怠管理簿に出す対象を、画面の編集内容から求める(勤怠締め業務フロー ⑥)。
///
/// 申請書を回収して画面で直した日 —— シフトまたは打刻を修正した日 —— だけを拾う。
/// 判定のやり直しは画面側で済んでいるため、ここでは修正後の値をそのまま読む。
/// </summary>
public static class AttendanceLedgerTargets
{
    /// <summary>修正のあった社員だけを、部門 → 氏名の順で返す。</summary>
    /// <param name="overtimeThresholdMinutes">
    /// 時間外として書き出すしきい値(分)。判定閾値マスタと同じ値を渡す。
    /// これ未満の超過は、通常の勤務の範囲として時間外欄に書かない。
    /// </param>
    public static List<AttendanceLedgerPerson> Build(ReportSheet sheet, int overtimeThresholdMinutes = 30)
    {
        var people = new List<AttendanceLedgerPerson>();

        foreach (var block in sheet.Employees)
        {
            var days = new List<AttendanceLedgerDay>();

            for (int i = 0; i < sheet.DayCount; i++)
            {
                if (!block.ShiftEdited[i] && !block.PunchEdited[i]) continue;
                if (sheet.DateOfDay(i + 1) is not { } date) continue;

                var punches = TimeText.Extract(block.Punch[i]);
                var judgement = block.Judgements[i];
                var shiftChange = ShiftChangeOf(sheet, block, date);

                days.Add(new AttendanceLedgerDay
                {
                    Day = i + 1,
                    ShiftText = shiftChange ?? PlannedText(block, i),
                    LeaveMark = LeaveMarkOf(block.Shift[i]),
                    StartText = punches.Count > 0 ? punches[0] : "",
                    EndText = punches.Count > 1 ? punches[^1] : "",
                    Mark = MarkOf(judgement.Label, shiftChange),
                    Reason = block.Note[i],
                    OvertimeMinutes = OvertimeOf(block, i, punches, overtimeThresholdMinutes),
                    WeekendOrHoliday = sheet.Holidays.IsWeekendOrHoliday(date)
                });
            }

            if (days.Count == 0) continue;

            people.Add(new AttendanceLedgerPerson
            {
                PersonName = block.Name,
                Department = block.Department,
                EmployeeNo = block.EmployeeNo,
                Days = days
            });
        }

        return people
            .OrderBy(p => p.Department)
            .ThenBy(p => p.PersonName)
            .ToList();
    }

    /// <summary>
    /// 「公休→出勤」のような書き方。修正履歴にシフトの変更が残っている日だけ作る。
    /// 打刻だけを直した日は null(予定シフトをそのまま書く)。
    /// </summary>
    private static string? ShiftChangeOf(ReportSheet sheet, ReportEmployeeBlock block, DateOnly date)
    {
        var change = sheet.History.Entries
            .Where(e => e.WorkDate == date && e.PersonName == block.Name && e.Field == "シフト")
            .LastOrDefault();
        if (change == null) return null;

        var before = KindNameOf(change.Before);
        var after = KindNameOf(change.After);
        return before == after ? null : $"{before}→{after}";
    }

    /// <summary>シフト表のセルの値を、勤怠管理簿の言い方に直す。</summary>
    private static string KindNameOf(string shiftValue) => shiftValue.Trim() switch
    {
        "" => "なし",
        "公" => "公休",
        "有" => "有休",
        "欠" => "欠勤",
        "特" => "特別",
        "半" => "半休",
        "出張" => "出張",
        "本部" => "出張",
        var v when TimeText.Extract(v).Count > 0 => "出勤",
        var v => v
    };

    /// <summary>予定シフトの「7:00 ～16:00」表記。予定終了が無ければ開始だけ。</summary>
    private static string PlannedText(ReportEmployeeBlock block, int index)
    {
        var start = block.Shift[index].Trim();
        if (start.Length == 0) return "";
        if (TimeText.Extract(start).Count == 0) return KindNameOf(start);

        var end = block.PlannedEnd[index].Trim();
        return end.Length > 0 ? $"{start} ～{end}" : start;
    }

    /// <summary>有休･欠勤 / 出張･特別 の欄に書く1文字。</summary>
    private static string LeaveMarkOf(string shiftValue) => shiftValue.Trim() switch
    {
        "有" => "有",
        "欠" => "欠",
        "特" => "特",
        "出張" or "本部" => "出",
        _ => ""
    };

    /// <summary>遅刻･早退･外出 / 振替休日変更 の欄に書く1文字。</summary>
    private static string MarkOf(string judgementLabel, string? shiftChange)
    {
        // 公休と出勤の入れ替えは「振替休日変更」
        if (shiftChange is { } change && (change.StartsWith("公休→") || change.EndsWith("→公休"))) return "振";

        return judgementLabel switch
        {
            "遅" => "遅",
            "早" => "早",
            _ => ""
        };
    }

    /// <summary>
    /// 予定終了を超えた分(時間外労働時間)。
    /// しきい値未満の超過は、判定でも時間外にしていないため0とする。
    /// </summary>
    private static int OvertimeOf(ReportEmployeeBlock block, int index, IReadOnlyList<string> punches, int threshold)
    {
        if (punches.Count < 2) return 0;
        if (!TimeText.TryParse(punches[^1], out var actualOut)) return 0;
        if (!TimeText.TryParse(block.PlannedEnd[index], out var plannedEnd)) return 0;

        var minutes = (int)(actualOut - plannedEnd).TotalMinutes;
        return minutes >= threshold ? minutes : 0;
    }
}
