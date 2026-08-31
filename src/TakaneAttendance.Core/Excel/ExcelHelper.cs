using NPOI.SS.UserModel;
using NPOI.HSSF.UserModel;
using NPOI.XSSF.UserModel;

namespace TakaneAttendance.Core.Excel;

/// <summary>
/// NPOI のセル読み取りを1か所に集約する。
/// .xls(HSSF) と .xlsx(XSSF) の差異、数式セル、日付/時刻セルの扱いをここで吸収する。
/// </summary>
public static class ExcelHelper
{
    private static readonly DataFormatter Formatter = new();

    /// <summary>拡張子を見てブックを開く。読み取り専用でストリームを閉じる。</summary>
    public static IWorkbook OpenWorkbook(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".xls" => new HSSFWorkbook(fs),
            ".xlsx" or ".xlsm" => new XSSFWorkbook(fs),
            _ => WorkbookFactory.Create(fs)
        };
    }

    public static IReadOnlyList<string> SheetNames(string path)
    {
        using var wb = OpenWorkbook(path);
        var names = new List<string>();
        for (int i = 0; i < wb.NumberOfSheets; i++) names.Add(wb.GetSheetName(i));
        return names;
    }

    /// <summary>セルを画面表示と同じ文字列にする。空セルは空文字。</summary>
    public static string Text(ICell? cell)
    {
        if (cell == null) return string.Empty;
        try
        {
            if (cell.CellType == CellType.Formula)
            {
                return cell.CachedFormulaResultType switch
                {
                    CellType.String  => cell.StringCellValue?.Trim() ?? "",
                    CellType.Numeric => NumericToText(cell),
                    CellType.Boolean => cell.BooleanCellValue.ToString(),
                    _ => ""
                };
            }
            if (cell.CellType == CellType.Numeric) return NumericToText(cell);
            return Formatter.FormatCellValue(cell).Trim();
        }
        catch { return string.Empty; }
    }

    private static string NumericToText(ICell cell)
    {
        if (DateUtil.IsCellDateFormatted(cell))
        {
            var t = TimeOfDay(cell);
            if (t.HasValue) return $"{(int)t.Value.TotalHours}:{t.Value.Minutes:00}";
        }
        var v = cell.NumericCellValue;
        return v == Math.Floor(v) ? ((long)v).ToString() : v.ToString("0.####");
    }

    public static string Text(IRow? row, int col) => Text(row?.GetCell(col));

    public static bool IsEmpty(ICell? cell) => string.IsNullOrWhiteSpace(Text(cell));

    /// <summary>
    /// 日付書式が設定された数値セルから時刻部分を取り出す。
    /// Excel のシリアル値は 1.0 = 1日なので、小数部×1440 を分に換算する。
    /// </summary>
    public static TimeSpan? TimeOfDay(ICell? cell)
    {
        if (cell == null) return null;
        double v;
        if (cell.CellType == CellType.Numeric) v = cell.NumericCellValue;
        else if (cell.CellType == CellType.Formula && cell.CachedFormulaResultType == CellType.Numeric) v = cell.NumericCellValue;
        else return null;

        if (!DateUtil.IsCellDateFormatted(cell)) return null;

        double frac = v - Math.Floor(v);
        int minutes = (int)Math.Round(frac * 1440.0);
        if (minutes is < 0 or > 1440) return null;
        return TimeSpan.FromMinutes(minutes);
    }

    /// <summary>整数として読めれば返す。"1" のような文字列セルにも対応する。</summary>
    public static int? AsInt(ICell? cell)
    {
        if (cell == null) return null;
        if (cell.CellType == CellType.Numeric && !DateUtil.IsCellDateFormatted(cell))
        {
            var v = cell.NumericCellValue;
            if (Math.Abs(v - Math.Round(v)) < 1e-9) return (int)Math.Round(v);
            return null;
        }
        var s = Text(cell);
        return int.TryParse(s.Trim(), out var n) ? n : null;
    }

    /// <summary>列番号(0始まり)を A, B, ... AA のような列名にする。</summary>
    public static string ColumnName(int col)
    {
        var s = string.Empty;
        for (int n = col; n >= 0; n = n / 26 - 1) s = (char)('A' + n % 26) + s;
        return s;
    }

    /// <summary>エラー追跡用のセル位置表記(例: 7月修正!G32)。</summary>
    public static string CellRef(ISheet sheet, int row, int col)
        => $"{sheet.SheetName}!{ColumnName(col)}{row + 1}";

    /// <summary>
    /// 「1, 2, 3, ...」と連番が並ぶ行(日番号行)を探す。
    /// シフト表・打刻データとも日付列の位置がファイルによって異なるため、レイアウトを決め打ちしない。
    /// </summary>
    /// <param name="minRun">連番として認める最小の長さ</param>
    public static DayHeader? FindDayNumberRow(ISheet sheet, int scanRows = 60, int scanCols = 60, int minRun = 5)
    {
        int lastRow = Math.Min(sheet.LastRowNum, scanRows);
        for (int r = 0; r <= lastRow; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            for (int startCol = 0; startCol <= scanCols; startCol++)
            {
                if (AsInt(row.GetCell(startCol)) != 1) continue;

                var days = new List<int> { 1 };
                int c = startCol + 1;
                while (c <= scanCols)
                {
                    var n = AsInt(row.GetCell(c));
                    if (n != days.Count + 1) break;
                    days.Add(n.Value);
                    c++;
                }
                if (days.Count >= minRun)
                    return new DayHeader(r, startCol, days.Count);
            }
        }
        return null;
    }
}

/// <summary>日番号行の位置情報。</summary>
/// <param name="RowIndex">日番号が並ぶ行(0始まり)</param>
/// <param name="StartColumn">1日目の列(0始まり)</param>
/// <param name="DayCount">連番の長さ(=対象日数)</param>
public sealed record DayHeader(int RowIndex, int StartColumn, int DayCount);
