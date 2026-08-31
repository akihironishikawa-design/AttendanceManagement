using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Reporting;

/// <summary>
/// 申請書の対象者を突合結果から求める(勤怠締め業務フロー STEP1 ④)。
///
/// 申請書マスタ(application_form.xml)の対応表をそのまま使い、
/// 様式のある3種類(タイムカード修正届出書・年次有休休暇・欠勤申請書・出張届)だけを取り出す。
/// 勤怠管理簿など様式を同梱していない申請書は、これまでどおり
/// 申請書 確認一覧(<see cref="ApplicationFormReport"/>)で確認する。
/// </summary>
public static class ApplicationFormTargets
{
    /// <summary>申請書1枚分ずつの対象を作る。並びは 部門 → 氏名 → 日付。</summary>
    public static List<ApplicationFormEntry> Build(MatchingResult result, ApplicationFormMaster? master)
    {
        var entries = new List<ApplicationFormEntry>();
        if (master == null) return entries;

        var details = result.Details
            .OrderBy(d => d.Department)
            .ThenBy(d => d.PersonName)
            .ThenBy(d => d.WorkDate);

        foreach (var d in details)
        {
            foreach (var form in master.Resolve(d.ResultCodes))
            {
                if (ApplicationFormKinds.FromName(form.FormName) is not { } kind) continue;
                entries.Add(Create(kind, d, form.Reason));
            }
        }

        return MergeContinuousLeave(entries);
    }

    /// <summary>
    /// 勤怠管理簿の対象(画面で修正した日がある社員)を、申請書一覧と同じ形にそろえる。
    /// 突合結果ではなく、画面の編集内容(<see cref="ReportSheet"/>)から作る。
    /// </summary>
    public static List<ApplicationFormEntry> BuildLedger(ReportSheet? sheet, int overtimeThresholdMinutes = 30)
    {
        if (sheet == null) return new List<ApplicationFormEntry>();

        return AttendanceLedgerTargets.Build(sheet, overtimeThresholdMinutes).Select(p =>
        {
            var first = p.Days.Min(d => d.Day);
            var last = p.Days.Max(d => d.Day);
            return new ApplicationFormEntry
            {
                Kind = ApplicationFormKind.AttendanceLedger,
                PersonName = p.PersonName,
                Department = p.Department,
                EmployeeNo = p.EmployeeNo,
                FromDate = new DateOnly(sheet.Year, sheet.Month, first),
                ToDate = new DateOnly(sheet.Year, sheet.Month, last),
                Reason = "画面で修正した日",
                Ledger = p
            };
        }).ToList();
    }

    private static ApplicationFormEntry Create(ApplicationFormKind kind, AttendanceDaily d, string reason)
        => new()
        {
            Kind = kind,
            PersonName = d.PersonName,
            Department = d.Department,
            EmployeeNo = d.EmployeeNo,
            FromDate = d.WorkDate,
            ToDate = d.WorkDate,
            Reason = reason,
            PlannedStart = d.Shift?.PlannedStart,
            PlannedEnd = d.Shift?.PlannedEnd,
            ActualIn = d.Punch?.ActualIn,
            ActualOut = d.Punch?.ActualOut,
            LateMinutes = Diff(d.Punch?.ActualIn, d.Shift?.PlannedStart),
            EarlyLeaveMinutes = Diff(d.Shift?.PlannedEnd, d.Punch?.ActualOut),
            AllDay = d.ResultCodes.Contains(ResultCode.BusinessTripFull),
            ShiftText = d.ShiftText,
            PunchText = PunchTextOf(d)
        };

    /// <summary>遅刻・早退の分。超過していない場合は0。</summary>
    private static int Diff(TimeSpan? later, TimeSpan? earlier)
    {
        if (later is not { } a || earlier is not { } b) return 0;
        var minutes = (int)(a - b).TotalMinutes;
        return minutes > 0 ? minutes : 0;
    }

    private static string PunchTextOf(AttendanceDaily d)
    {
        if (d.Punch is not { PunchCount: > 0 }) return "-";
        var first = d.FirstPunchText;
        var last = d.LastPunchText;
        return last == "-" || last == first ? first : $"{first} 〜 {last}";
    }

    /// <summary>
    /// 年次有休休暇・欠勤申請書は、同じ人・同じ理由で続いている日を1枚にまとめる。
    /// 様式が「令和○年○月○日 〜 令和○年○月○日」「○日間」の書き方のため。
    /// </summary>
    private static List<ApplicationFormEntry> MergeContinuousLeave(List<ApplicationFormEntry> entries)
    {
        var merged = new List<ApplicationFormEntry>();

        foreach (var group in entries.Where(e => e.Kind == ApplicationFormKind.PaidLeave)
                                     .GroupBy(e => (e.PersonName, e.Reason)))
        {
            ApplicationFormEntry? open = null;
            foreach (var e in group.OrderBy(e => e.FromDate))
            {
                if (open != null && e.FromDate.DayNumber == open.ToDate.DayNumber + 1)
                {
                    open = Extend(open, e.ToDate);
                    continue;
                }
                if (open != null) merged.Add(open);
                open = e;
            }
            if (open != null) merged.Add(open);
        }

        merged.AddRange(entries.Where(e => e.Kind != ApplicationFormKind.PaidLeave));

        return merged
            .OrderBy(e => e.Department)
            .ThenBy(e => e.PersonName)
            .ThenBy(e => e.FromDate)
            .ToList();
    }

    private static ApplicationFormEntry Extend(ApplicationFormEntry e, DateOnly to) => new()
    {
        Kind = e.Kind,
        PersonName = e.PersonName,
        Department = e.Department,
        EmployeeNo = e.EmployeeNo,
        FromDate = e.FromDate,
        ToDate = to,
        Reason = e.Reason,
        PlannedStart = e.PlannedStart,
        PlannedEnd = e.PlannedEnd,
        ActualIn = e.ActualIn,
        ActualOut = e.ActualOut,
        LateMinutes = e.LateMinutes,
        EarlyLeaveMinutes = e.EarlyLeaveMinutes,
        AllDay = e.AllDay,
        ShiftText = e.ShiftText,
        PunchText = e.PunchText
    };
}
