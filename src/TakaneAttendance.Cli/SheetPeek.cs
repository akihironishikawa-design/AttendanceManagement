using NPOI.SS.UserModel;
using TakaneAttendance.Core.Excel;

namespace TakaneAttendance.Cli;

/// <summary>
/// ブックの中身を行単位でそのまま表示する。
///
/// お客様からお預かりした帳票テンプレートや従業員マスタの様式を確かめるために使う。
/// レイアウトの自動検出を通さず、セルの値を見たままに出す。
/// </summary>
internal static class SheetPeek
{
    public static int Run(string path, string? sheetName, int rows, int cols, bool showFormula = false)
    {
        using var wb = ExcelHelper.OpenWorkbook(path);

        var sheets = sheetName == null
            ? Enumerable.Range(0, wb.NumberOfSheets).Select(wb.GetSheetAt).ToList()
            : new List<ISheet> { wb.GetSheet(sheetName) ?? throw new ArgumentException($"シート '{sheetName}' がありません。") };

        Console.WriteLine($"ファイル : {Path.GetFileName(path)}");
        Console.WriteLine($"シート数 : {wb.NumberOfSheets}");
        Console.WriteLine();

        foreach (var sheet in sheets)
        {
            if (sheet == null) continue;

            Console.WriteLine($"================ [{sheet.SheetName}] ================");
            Console.WriteLine($"  行 0〜{sheet.LastRowNum} / 結合 {sheet.NumMergedRegions} 箇所");

            int shown = 0;
            for (int r = sheet.FirstRowNum; r <= sheet.LastRowNum && shown < rows; r++)
            {
                var row = sheet.GetRow(r);
                if (row == null) continue;

                var values = new List<string>();
                int last = Math.Min(row.LastCellNum, cols);
                for (int c = 0; c < last; c++)
                {
                    var cell = row.GetCell(c);
                    var text = showFormula && cell is { CellType: CellType.Formula }
                        ? "=" + cell.CellFormula
                        : ExcelHelper.Text(cell).Replace("\n", "\\n");
                    values.Add(text.Length > 40 ? text[..40] + "…" : text);
                }

                // 空行は出さない(様式の空白が続く帳票が多いため)
                if (values.All(v => v.Length == 0)) continue;

                Console.WriteLine($"  r{r,-4}| " + string.Join(" | ", values.Select(v => Pad(v, showFormula ? 26 : 14))));
                shown++;
            }

            Console.WriteLine();
        }
        return 0;
    }

    /// <summary>全角を2文字ぶんとして数え、列をそろえる。</summary>
    private static string Pad(string value, int width)
    {
        int visible = value.Sum(ch => ch < 0x80 ? 1 : 2);
        return value + new string(' ', Math.Max(0, width - visible));
    }
}
