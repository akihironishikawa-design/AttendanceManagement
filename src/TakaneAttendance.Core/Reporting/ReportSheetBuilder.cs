using NPOI.SS.UserModel;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Naming;

namespace TakaneAttendance.Core.Reporting;

/// <summary>テンプレートから読み取った社員ブロック1件分。</summary>
public sealed record ReportTemplateEmployee(string EmployeeNo, string Name, string Department, string Key);

/// <summary>出席記録レポートのテンプレート・帳票で共通に使うレイアウト操作。</summary>
internal static class ReportLayout
{
    /// <summary>「出席記録」を含むシート。見つからなければ先頭シート。</summary>
    public static ISheet? FindReportSheet(IWorkbook wb)
    {
        for (int i = 0; i < wb.NumberOfSheets; i++)
            if (wb.GetSheetName(i).Contains("出席記録")) return wb.GetSheetAt(i);
        return wb.NumberOfSheets > 0 ? wb.GetSheetAt(0) : null;
    }

    /// <summary>「名前:」のようなラベルセルの右隣にある値を取る。</summary>
    public static string ValueAfterLabel(IRow row, string label)
    {
        int last = Math.Max((int)row.LastCellNum, 1);
        for (int c = 0; c < last; c++)
        {
            if (!ExcelHelper.Text(row, c).Contains(label)) continue;
            for (int k = c + 1; k < last && k <= c + 6; k++)
            {
                var v = ExcelHelper.Text(row, k);
                if (v.Length > 0) return v;
            }
        }
        return string.Empty;
    }

    public static string DayOfWeekText(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Sunday => "日", DayOfWeek.Monday => "月", DayOfWeek.Tuesday => "火",
        DayOfWeek.Wednesday => "水", DayOfWeek.Thursday => "木", DayOfWeek.Friday => "金", _ => "土"
    };
}

/// <summary>出席記録レポートのテンプレートから社員の並び順を読み取る。</summary>
public static class ReportTemplateReader
{
    /// <summary>
    /// テンプレートに載っている社員を、書かれている順に返す。
    /// 作業番号の補完に使うだけなので、社員が1人も書かれていない空のテンプレートでも構わない。
    /// </summary>
    public static IReadOnlyList<ReportTemplateEmployee> ReadEmployees(string templatePath)
    {
        var list = new List<ReportTemplateEmployee>();
        using var wb = ExcelHelper.OpenWorkbook(templatePath);

        var sheet = ReportLayout.FindReportSheet(wb);
        if (sheet == null) return list;

        for (int r = ReportFormat.FirstBlockRow; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;
            if (!ExcelHelper.Text(row, 0).Contains("作業番号")) continue;

            var name = ReportLayout.ValueAfterLabel(row, "名前");
            if (name.Length == 0) continue;

            list.Add(new ReportTemplateEmployee(
                ReportLayout.ValueAfterLabel(row, "作業番号"),
                name,
                ReportLayout.ValueAfterLabel(row, "部門"),
                NameNormalizer.Normalize(name)));
        }
        return list;
    }
}

/// <summary>
/// 突合結果を、出席記録レポートのレイアウト(<see cref="ReportSheet"/>)に展開する。
///
/// 社員一覧・並び順・部門は「シフト表の記載どおり」。
/// 締めの対象はシフト表に載っている社員のため、テンプレートは書式・レイアウトの雛形と
/// 作業番号の補完にだけ使い、テンプレートにしかいない社員は出力しない。
/// </summary>
public static class ReportSheetBuilder
{
    /// <param name="templatePath">
    /// 帳票テンプレート。作業番号を補完するために社員一覧を読む。null でも構わない。
    /// </param>
    public static ReportSheet Build(MatchingResult result, string? templatePath)
    {
        int year = result.TargetYear, month = result.TargetMonth;
        int dayCount = year == 0 ? 0 : DateTime.DaysInMonth(year, month);

        var sheet = new ReportSheet { Year = year, Month = month, DayCount = dayCount };

        // 祝日の見出し色と休場日のグレー表示に使う(仕様書 v3.0 第15章)
        if (result.Masters?.Holidays is { } holidays) sheet.Holidays = holidays;

        if (year == 0)
        {
            sheet.Messages.Add("[E-RP-006] 対象年月が確定していないため帳票を組み立てられません。");
            return sheet;
        }

        // ---- 突合結果を「社員 → 日」で引けるようにする ----
        var byPerson = result.Details
            .GroupBy(x => x.Person.Key)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.WorkDate.Day, x => x));

        // ---- テンプレートの社員一覧(作業番号の補完に使う) ----
        var templateByKey = new Dictionary<string, ReportTemplateEmployee>();
        if (!string.IsNullOrWhiteSpace(templatePath) && File.Exists(templatePath))
        {
            try
            {
                foreach (var t in ReportTemplateReader.ReadEmployees(templatePath))
                    templateByKey.TryAdd(t.Key, t);
            }
            catch (Exception ex)
            {
                sheet.Messages.Add($"テンプレートの社員一覧を読めませんでした({ex.Message})。作業番号は打刻データからのみ補完します。");
            }
        }

        // 作業番号は打刻データにしかないため、突合結果から引く
        var personByKey = new Dictionary<string, Models.PersonRef>();
        foreach (var d in result.Details) personByKey.TryAdd(d.Person.Key, d.Person);

        // ---- 打刻データの社員一覧。氏名・部門・並び順の基準にする ----
        // お客様が手作業で作られている出席記録はタイムレコーダー出力の複製で、
        // 並び順(作業番号ブロック順)も氏名(原田　祥吾)も部門(ハウス)も打刻データ側の表記になっている。
        var punchByKey = new Dictionary<string, Models.PunchRosterEntry>();
        foreach (var p in result.PunchRoster) punchByKey.TryAdd(p.Key, p);

        // ---- 出力対象: シフト表に載っている社員を、打刻データの記載順で ----
        // 打刻データに居ない方(シフトのみの方)は、その後ろにシフト表の記載順で置く。
        var used = new HashSet<string>();
        var roster = new List<ShiftRosterEntry>();
        foreach (var r in result.ShiftRoster)
            if (used.Add(r.Key)) roster.Add(r);

        foreach (var r in roster
                     .OrderBy(r => punchByKey.TryGetValue(r.Key, out var p) ? p.Order : int.MaxValue)
                     .ThenBy(r => r.Order))
        {
            personByKey.TryGetValue(r.Key, out var person);
            templateByKey.TryGetValue(r.Key, out var template);
            punchByKey.TryGetValue(r.Key, out var punchEntry);

            var employeeNo = punchEntry is { EmployeeNo.Length: > 0 }
                ? punchEntry.EmployeeNo
                : person?.EmployeeNo ?? template?.EmployeeNo ?? "";
            var department = punchEntry is { Department.Length: > 0 }
                ? punchEntry.Department
                : r.Department.Length > 0 ? r.Department : template?.Department ?? "";

            sheet.Employees.Add(NewBlock(
                employeeNo,
                punchEntry?.DisplayName ?? r.DisplayName,
                department,
                r.Key,
                hasData: byPerson.ContainsKey(r.Key), added: false, dayCount, person));
        }

        if (sheet.Employees.Count == 0)
        {
            sheet.Messages.Add("シフト表から社員を読み取れなかったため、突合結果の社員で出力します。");
            foreach (var key in byPerson.Keys)
            {
                if (!used.Add(key)) continue;
                var any = result.Details.First(x => x.Person.Key == key);
                sheet.Employees.Add(NewBlock(any.Person.EmployeeNo ?? "", any.PersonName, any.Department, key,
                                             hasData: true, added: true, dayCount, any.Person));
            }
        }

        // ---- セル値の展開 ----
        foreach (var block in sheet.Employees)
        {
            if (!byPerson.TryGetValue(block.Key, out var days)) continue;

            for (int d = 1; d <= dayCount; d++)
            {
                if (!days.TryGetValue(d, out var a)) continue;

                // シフト行: 勤務区分の文字値と、通常勤務の予定開始時刻。
                // 予定開始時刻は画面で矛盾を見るために持つ。帳票へは現行様式どおり出力しない
                // (AttendanceReportWriter が時刻のセルを飛ばす)。
                if (a.Shift is { RawValue.Length: > 0 } s)
                    block.Shift[d - 1] = s.RawValue;

                // 打刻行: 原文をそのまま(丸めない)
                if (a.Punch is { RawValue.Length: > 0 } p)
                    block.Punch[d - 1] = p.RawValue;

                // 予定終了時刻は画面での早退・時間外の判定に使う(帳票へは出力しない)
                if (a.Shift?.PlannedEnd is { } end)
                    block.PlannedEnd[d - 1] = $"{(int)end.TotalHours}:{end.Minutes:00}";

                // 突合時の判定をそのまま持たせる(画面のセルの色分けと理由表示に使う)
                block.Judgements[d - 1] = new CellJudgement(a.Judgement, a.JudgementLabel, a.ResultText);
            }
        }

        return sheet;
    }

    private static ReportEmployeeBlock NewBlock(
        string employeeNo, string name, string department, string key,
        bool hasData, bool added, int dayCount, Models.PersonRef? person)
        => new(dayCount)
        {
            EmployeeNo = employeeNo,
            Name = name,
            Department = department,
            Key = key,
            HasMatchingData = hasData,
            AddedFromData = added,
            Person = person
        };
}
