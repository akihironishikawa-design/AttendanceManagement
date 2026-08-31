using NPOI.SS.UserModel;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Naming;
using TakaneAttendance.Core.Parsing;

namespace TakaneAttendance.Core.Reporting;

/// <summary>
/// パート・アルバイト給与計算表(統合仕様書 v3.0 第16章)。
///
/// 様式は1人1シートで、日ごとに 出社時間 / 退社時間 / 休憩時間 を入れると、
/// 丸め後の時刻・勤務時間・総労働時間・時間外がテンプレート側の数式で求まる。
///
/// このアプリが書き込むのは次の3つだけで、既存の数式・書式には触らない。
///   ・出社時間 … 打刻1回目(丸めない実打刻)
///   ・退社時間 … 最終打刻(丸めない実打刻)
///   ・休憩時間 … 丸め後拘束時間から求めた休憩(仕様書 14.3)
///
/// 休憩は現在 手入力で運用されている欄で、そこを自動計算で埋めるのがこの帳票の要点になる。
///
/// 出力対象は雇用区分がパート・アルバイトの社員のみ(正社員のシートは作らない)。
/// </summary>
public static class PartTimePayrollWriter
{
    // ---- 様式の位置(0始まり) ----
    private const int MonthRow = 0;   // 「4月」
    private const int MonthCol = 0;
    private const int NameRow = 1;    // 氏名 / 基本時給
    private const int NameCol = 1;
    private const int WageCol = 6;
    private const int DayCol = 0;     // 日番号
    private const int WeekCol = 1;    // 曜日(土日祝の判定に使われる)

    /// <summary>様式の中で下敷きに使うシート(白紙の様式)。</summary>
    private const string BaseSheetName = "原本";

    public static ReportOutputResult Write(
        MatchingResult result, string templatePath, string outputPath, BreakRuleMaster breakRule)
    {
        var output = new ReportOutputResult { ReportName = "パート・アルバイト給与計算表", Path = outputPath };

        if (!File.Exists(templatePath))
        {
            output.Messages.Add($"[{ErrorCodes.FileMissing}] 給与計算表のテンプレートが見つかりません: {templatePath}");
            return output;
        }

        // 対象はパート・アルバイトのみ。正社員のシートは生成しない(仕様書 第16章)。
        var targets = result.Details
            .Where(d => EmployeeMaster.IsPartTimePayroll(d.Person.Employment))
            .GroupBy(d => d.Person.Key)
            .OrderBy(g => g.First().PersonName)
            .ToList();

        if (targets.Count == 0)
        {
            output.Messages.Add("パート・アルバイトの対象者がいません。" +
                                "「マスタを編集」のパート・アルバイトタブで対象者を登録してください。");
            return output;
        }

        File.Copy(templatePath, outputPath, overwrite: true);

        using (var wb = ExcelHelper.OpenWorkbook(outputPath))
        {
            int baseIndex = wb.GetSheetIndex(BaseSheetName);
            if (baseIndex < 0)
            {
                output.Messages.Add($"[{ErrorCodes.StructureMissing}] 様式に「{BaseSheetName}」シートがありません。");
                return output;
            }

            var holidays = result.Masters?.Holidays ?? new HolidayMaster();
            var employees = result.Masters?.Employees;
            var usedNames = new HashSet<string>();

            for (int i = 0; i < targets.Count; i++)
            {
                var days = targets[i].ToList();
                var person = days[0];

                // 1人1シート。最後の1人は下敷きのシートをそのまま使う
                var sheet = i == targets.Count - 1 ? wb.GetSheetAt(baseIndex) : wb.CloneSheet(baseIndex);
                wb.SetSheetName(wb.GetSheetIndex(sheet), SheetName(person.PersonName, usedNames));

                var layout = FindLayout(sheet);
                if (layout == null)
                {
                    output.Messages.Add($"[{ErrorCodes.StructureMissing}] {sheet.SheetName} の見出し(出社時間・退社時間)を検出できません。");
                    continue;
                }

                var (headerRow, colIn, colOut, colBreak) = layout.Value;

                WriteHeader(sheet, person.PersonName, employees?.HourlyWageOf(targets[i].Key),
                            result.TargetYear, result.TargetMonth, output);
                WriteDays(sheet, headerRow, result.TargetYear, result.TargetMonth, holidays);

                int written = WritePerson(sheet, headerRow, colIn, colOut, colBreak, days, breakRule, holidays);

                output.WrittenEmployees++;
                output.WrittenCells += written;
            }

            // 並びを氏名の順にそろえる(最後の1人が下敷きの位置に残るため)
            if (targets.Count > 1) wb.SetSheetOrder(wb.GetSheetName(wb.NumberOfSheets - 1), 0);
            wb.SetActiveSheet(0);

            // Excel で開いたときに数式を計算し直させる(値だけ差し替えたため)
            for (int i = 0; i < wb.NumberOfSheets; i++)
                wb.GetSheetAt(i).ForceFormulaRecalculation = true;
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            wb.Write(fs);
        }

        output.Success = true;
        return output;
    }

    /// <summary>シート名(31文字まで・記号は使えない)。</summary>
    private static string SheetName(string personName, HashSet<string> used)
    {
        var name = personName;
        foreach (var c in new[] { '[', ']', ':', '*', '?', '/', '\\' }) name = name.Replace(c, '_');
        if (name.Length > 28) name = name[..28];
        if (name.Length == 0) name = "社員";

        var unique = name;
        for (int n = 2; !used.Add(unique); n++) unique = $"{name}{n}";
        return unique;
    }

    /// <summary>対象月・氏名・基本時給を書く。</summary>
    private static void WriteHeader(ISheet sheet, string personName, int? hourlyWage,
                                    int year, int month, ReportOutputResult output)
    {
        SetText(sheet, MonthRow, MonthCol, $"{month}月");
        SetText(sheet, NameRow, NameCol, personName);

        if (hourlyWage is { } wage) SetNumber(sheet, NameRow, WageCol, wage);
        else output.Messages.Add($"{personName} の基本時給が未登録のため、時給欄は空欄のままです。");
    }

    /// <summary>日番号と曜日を対象月に合わせて書き直す(様式は16日〜翌15日の並びのため)。</summary>
    private static void WriteDays(ISheet sheet, int headerRow, int year, int month, HolidayMaster holidays)
    {
        int daysInMonth = DateTime.DaysInMonth(year, month);

        for (int i = 0; i < 31; i++)
        {
            int r = headerRow + 1 + i;
            var row = sheet.GetRow(r) ?? sheet.CreateRow(r);

            if (i < daysInMonth)
            {
                var date = new DateOnly(year, month, i + 1);
                SetNumber(sheet, r, DayCol, i + 1);
                // 土日祝の欄は曜日の文字で決まるため、祝日は「祝」と書く(様式の数式に合わせる)
                SetText(sheet, r, WeekCol, WeekOf(date, holidays));
            }
            else
            {
                Blank(row, DayCol);
                Blank(row, WeekCol);
            }
        }
    }

    private static string WeekOf(DateOnly date, HolidayMaster holidays)
        => holidays.Resolve(date) == DayKind.Holiday
            ? "祝"
            : date.DayOfWeek switch
            {
                DayOfWeek.Sunday => "日", DayOfWeek.Monday => "月", DayOfWeek.Tuesday => "火",
                DayOfWeek.Wednesday => "水", DayOfWeek.Thursday => "木", DayOfWeek.Friday => "金", _ => "土"
            };

    private static void SetText(ISheet sheet, int row, int col, string value)
        => Cell(sheet, row, col).SetCellValue(value);

    private static void SetNumber(ISheet sheet, int row, int col, double value)
        => Cell(sheet, row, col).SetCellValue(value);

    private static void Blank(IRow row, int col) => row.GetCell(col)?.SetCellType(CellType.Blank);

    private static ICell Cell(ISheet sheet, int row, int col)
    {
        var r = sheet.GetRow(row) ?? sheet.CreateRow(row);
        return r.GetCell(col) ?? r.CreateCell(col);
    }

    private static int WritePerson(
        ISheet sheet, int headerRow, int colIn, int colOut, int colBreak,
        List<AttendanceDaily> days, BreakRuleMaster breakRule, HolidayMaster holidays)
    {
        var byDay = days.ToDictionary(d => d.WorkDate.Day, d => d);
        int written = 0;

        for (int r = headerRow + 1; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            // A列の日番号で対象日を引く。「合計」行に来たら終わり。
            var day = ExcelHelper.AsInt(row.GetCell(0));
            if (day is null or < 1 or > 31) continue;
            if (!byDay.TryGetValue(day.Value, out var d)) continue;

            // 打刻2件以上の日だけ書く(打刻漏れの日は空欄のままにして、確認の対象を残す)
            if (d.Punch is not { PunchCount: >= 2 }) continue;
            if (d.Punch.ActualIn is not { } inTime || d.Punch.ActualOut is not { } outTime) continue;

            SetTime(row, colIn, inTime);
            SetTime(row, colOut, outTime);

            // 休憩は丸め後拘束時間から求める(仕様書 14.2 の手順)
            var work = breakRule.Calculate(inTime, outTime);
            if (work != null) SetTime(row, colBreak, TimeSpan.FromMinutes(work.BreakMinutes));

            written += 3;
        }
        return written;
    }

    /// <summary>時刻を「時刻の値」として書く。書式はテンプレートのものをそのまま使う。</summary>
    private static void SetTime(IRow row, int col, TimeSpan value)
    {
        var cell = row.GetCell(col) ?? row.CreateCell(col);
        // Excel の時刻は1日を1とする小数。書式(h:mm)はテンプレート側に付いている。
        cell.SetCellValue(value.TotalDays);
    }

    /// <summary>「出社時間」「退社時間」「休憩時間」の見出し位置を探す。</summary>
    private static (int HeaderRow, int ColIn, int ColOut, int ColBreak)? FindLayout(ISheet sheet)
    {
        for (int r = 0; r <= Math.Min(sheet.LastRowNum, 12); r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            int colIn = -1, colOut = -1, colBreak = -1;
            for (int c = 0; c < row.LastCellNum; c++)
            {
                switch (ExcelHelper.Text(row.GetCell(c)))
                {
                    case "出社時間": colIn = c; break;
                    case "退社時間": colOut = c; break;
                    case "休憩時間": colBreak = c; break;
                }
            }
            if (colIn >= 0 && colOut >= 0 && colBreak >= 0) return (r, colIn, colOut, colBreak);
        }
        return null;
    }
}
