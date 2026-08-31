using NPOI.SS.UserModel;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Naming;

namespace TakaneAttendance.Core.Reporting;

/// <summary>帳票1件の書き込み結果。</summary>
public sealed class ReportOutputResult
{
    public required string ReportName { get; init; }
    public required string Path { get; init; }
    public bool Success { get; set; }
    public int WrittenEmployees { get; set; }
    public int WrittenCells { get; set; }
    public List<string> Messages { get; } = new();

    public string Summary => Success
        ? $"{ReportName} : {WrittenEmployees} 名 / {WrittenCells} セル → {Path}"
        : $"{ReportName} : 出力できませんでした";
}

/// <summary>
/// 出勤簿(統合仕様書 v3.0 第16章)。
///
/// 様式は「氏名 / 部署 / 1〜31日」の月間一覧で、セルには勤務区分の文字値(公・有 など)が入る。
/// テンプレート原本を複製して値だけ書き込み、既存の数式・書式・印刷設定はそのまま残す。
///
/// 対象月のシートは、シート名に月が含まれるものを選ぶ(「７月」「7月」の全角・半角に対応)。
/// </summary>
public static class AttendanceBookWriter
{
    public static ReportOutputResult Write(ReportSheet sheet, string templatePath, string outputPath)
    {
        var result = new ReportOutputResult { ReportName = "出勤簿", Path = outputPath };

        if (!File.Exists(templatePath))
        {
            result.Messages.Add($"[{ErrorCodes.FileMissing}] 出勤簿のテンプレートが見つかりません: {templatePath}");
            return result;
        }

        // 原本は更新しない。複製したファイルを開いて書き込む。
        File.Copy(templatePath, outputPath, overwrite: true);

        using (var wb = ExcelHelper.OpenWorkbook(outputPath))
        {
            // 対象月のシートが無い様式(見本は7月だけ)は、先頭のシートを対象月に作り替える
            var target = FindMonthSheet(wb, sheet.Month);
            if (target == null)
            {
                target = wb.GetSheetAt(0);
                wb.SetSheetName(0, $"{sheet.Month}月");
                result.Messages.Add($"様式に{sheet.Month}月のシートが無いため、先頭のシートを{sheet.Month}月として作りました。");
            }

            var layout = FindLayout(target);
            if (layout == null)
            {
                result.Messages.Add($"[{ErrorCodes.StructureMissing}] 見出し行(氏名・部署・日番号)を検出できません。");
                return result;
            }

            var (headerRow, nameCol, deptCol, dayStartCol) = layout.Value;
            int lastDataRow = FindLastDataRow(target, headerRow, dayStartCol);

            WriteHeader(target, sheet, headerRow, dayStartCol);
            ClearData(target, headerRow, lastDataRow, nameCol, deptCol, dayStartCol);

            int row = headerRow + 1;
            foreach (var block in sheet.Employees)
            {
                if (!block.HasAnyValue) continue;
                if (row > lastDataRow)
                {
                    result.Messages.Add($"様式の行数({lastDataRow - headerRow} 行)を超えたため、" +
                                        $"{block.Name} 以降は書き込んでいません。");
                    break;
                }

                var targetRow = target.GetRow(row) ?? target.CreateRow(row);
                SetText(targetRow, nameCol, block.Name);
                SetText(targetRow, deptCol, block.Department);

                int written = 0;
                for (int d = 1; d <= sheet.DayCount; d++)
                {
                    var value = block.Shift[d - 1];
                    // 出勤簿には勤務区分の文字値だけを載せる(出勤した日は空欄のままにする)
                    if (value.Length == 0 || Parsing.TimeText.Extract(value).Count > 0) continue;

                    SetText(targetRow, dayStartCol + d - 1, value);
                    written++;
                }

                result.WrittenEmployees++;
                result.WrittenCells += written;
                row++;
            }

            // Excel で開いたときに数式を計算し直させる(値だけ差し替えたため)
            for (int i = 0; i < wb.NumberOfSheets; i++)
                wb.GetSheetAt(i).ForceFormulaRecalculation = true;
            using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
            wb.Write(fs);
        }

        result.Success = true;
        return result;
    }

    /// <summary>月・曜日・日番号の見出しを対象月に合わせて書き直す。</summary>
    private static void WriteHeader(ISheet sheet, ReportSheet report, int headerRow, int dayStartCol)
    {
        // 見出しの1行上が曜日、2行上が「○月」(見本の並び)
        int weekRow = headerRow - 1;
        int titleRow = headerRow - 2;

        if (titleRow >= 0) SetText(sheet.GetRow(titleRow) ?? sheet.CreateRow(titleRow), dayStartCol, $"{report.Month}月");

        var days = sheet.GetRow(headerRow) ?? sheet.CreateRow(headerRow);
        var weeks = weekRow >= 0 ? sheet.GetRow(weekRow) ?? sheet.CreateRow(weekRow) : null;

        for (int d = 1; d <= 31; d++)
        {
            int col = dayStartCol + d - 1;
            if (d <= report.DayCount)
            {
                SetNumber(days, col, d);
                if (weeks != null) SetText(weeks, col, report.DayOfWeekText(d));
            }
            else
            {
                Blank(days, col);
                if (weeks != null) Blank(weeks, col);
            }
        }
    }

    /// <summary>様式に見本として入っている氏名・部署・勤務区分を消す。</summary>
    private static void ClearData(ISheet sheet, int headerRow, int lastDataRow,
                                  int nameCol, int deptCol, int dayStartCol)
    {
        for (int r = headerRow + 1; r <= lastDataRow; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            Blank(row, nameCol);
            Blank(row, deptCol);
            for (int d = 0; d < 31; d++) Blank(row, dayStartCol + d);
        }
    }

    /// <summary>
    /// 社員を書ける最後の行。見出しの下から数式(合計)の行の手前まで。
    /// 合計行が見つからない場合はシートの最後まで使う。
    /// </summary>
    private static int FindLastDataRow(ISheet sheet, int headerRow, int dayStartCol)
    {
        for (int r = headerRow + 1; r <= sheet.LastRowNum; r++)
        {
            var cell = sheet.GetRow(r)?.GetCell(dayStartCol);
            if (cell is { CellType: CellType.Formula }) return r - 1;
        }
        return sheet.LastRowNum;
    }

    private static void SetText(IRow row, int col, string value)
        => (row.GetCell(col) ?? row.CreateCell(col)).SetCellValue(value);

    private static void SetNumber(IRow row, int col, int value)
        => (row.GetCell(col) ?? row.CreateCell(col)).SetCellValue(value);

    /// <summary>値だけ消す(罫線や書式は様式のまま残す)。</summary>
    private static void Blank(IRow row, int col)
        => row.GetCell(col)?.SetCellType(CellType.Blank);

    /// <summary>シート名に対象月が含まれるシートを選ぶ(「７月」「7月」「２０２６年 ４月」に対応)。</summary>
    private static ISheet? FindMonthSheet(IWorkbook wb, int month)
    {
        var candidates = new[] { $"{month}月", $"{ToFullWidth(month)}月" };

        for (int i = 0; i < wb.NumberOfSheets; i++)
        {
            var sheet = wb.GetSheetAt(i);
            if (sheet == null) continue;
            var name = sheet.SheetName.Replace(" ", "").Replace("　", "");
            if (candidates.Any(c => name.EndsWith(c) || name == c)) return sheet;
        }
        return null;
    }

    private static string ToFullWidth(int value)
        => new(value.ToString().Select(ch => (char)(ch - '0' + '０')).ToArray());

    /// <summary>見出し行(氏名・部署・日番号1..)の位置を探す。</summary>
    private static (int HeaderRow, int NameCol, int DeptCol, int DayStartCol)? FindLayout(ISheet sheet)
    {
        for (int r = 0; r <= Math.Min(sheet.LastRowNum, 10); r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            int nameCol = -1, deptCol = -1;
            for (int c = 0; c < row.LastCellNum; c++)
            {
                var text = ExcelHelper.Text(row.GetCell(c));
                if (text == "氏名") nameCol = c;
                else if (text is "部署" or "部門") deptCol = c;
            }
            if (nameCol < 0) continue;

            // 同じ行に 1,2,3... と連番が並ぶところが日列
            for (int c = Math.Max(nameCol, deptCol) + 1; c < row.LastCellNum - 3; c++)
            {
                if (ExcelHelper.AsInt(row.GetCell(c)) != 1) continue;
                if (ExcelHelper.AsInt(row.GetCell(c + 1)) != 2) continue;
                if (ExcelHelper.AsInt(row.GetCell(c + 2)) != 3) continue;
                return (r, nameCol, deptCol < 0 ? nameCol + 1 : deptCol, c);
            }
        }
        return null;
    }
}
