namespace TakaneAttendance.Core.Excel;

/// <summary>取り込むファイルの種類。</summary>
public enum WorkbookKind
{
    /// <summary>判別できない</summary>
    Unknown,
    /// <summary>シフト表</summary>
    Shift,
    /// <summary>タイムレコーダーの打刻データ</summary>
    Punch,
    /// <summary>出席記録レポートのテンプレート</summary>
    ReportTemplate
}

/// <summary>
/// ドラッグ＆ドロップされたブックが「シフト表 / 打刻データ / 帳票テンプレート」の
/// どれなのかを判別する。
///
/// 中身で判別する(ファイル名の付け方はお客様ごとに違うため)。
///   ・「作業番号」の行がある            → 打刻データ(タイムレコーダー出力)
///   ・「出席記録レポート」の表題だけある → 帳票テンプレート(中身が空の雛形)
///   ・日番号(1,2,3...)が並ぶ行がある     → シフト表
///
/// ただし提出済みの帳票は「作業番号」も持つため、ファイル名に
/// 「レポート」「テンプレート」を含む場合だけは、先にテンプレートとして扱う。
/// 中身を読めない場合(壊れたファイル・パスワード付きなど)はファイル名から推測する。
/// </summary>
public static class WorkbookClassifier
{
    private static readonly string[] ExcelExtensions = { ".xls", ".xlsx", ".xlsm" };

    /// <summary>Excel ブックとして開ける拡張子か。</summary>
    public static bool IsExcelFile(string path) =>
        ExcelExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static WorkbookKind Detect(string path)
    {
        if (!IsExcelFile(path) || !File.Exists(path)) return WorkbookKind.Unknown;

        var name = Path.GetFileNameWithoutExtension(path);

        // 提出済みの帳票は打刻データと同じ「作業番号」を持つため、名前で先に切り分ける
        if (name.Contains("テンプレート") || name.Contains("レポート")) return WorkbookKind.ReportTemplate;

        try
        {
            var byContent = DetectByContent(path);
            if (byContent != WorkbookKind.Unknown) return byContent;
        }
        catch (Exception)
        {
            // 開けないファイルは名前から推測する(理由は呼び出し元で表示する)
        }

        return DetectByFileName(name);
    }

    private static WorkbookKind DetectByContent(string path)
    {
        using var wb = ExcelHelper.OpenWorkbook(path);

        for (int i = 0; i < wb.NumberOfSheets && i < 8; i++)
        {
            var sheet = wb.GetSheetAt(i);
            bool hasEmployeeBlock = false, hasReportTitle = false;

            int lastRow = Math.Min(sheet.LastRowNum, 40);
            for (int r = 0; r <= lastRow && !hasEmployeeBlock; r++)
            {
                var row = sheet.GetRow(r);
                if (row == null) continue;

                int lastCol = Math.Min((int)row.LastCellNum, 32);
                for (int c = 0; c < lastCol; c++)
                {
                    var text = ExcelHelper.Text(row, c);
                    if (text.Length == 0) continue;
                    if (text.Contains("作業番号")) { hasEmployeeBlock = true; break; }
                    if (text.Contains("出席記録レポート")) hasReportTitle = true;
                }
            }

            if (hasEmployeeBlock) return WorkbookKind.Punch;
            if (hasReportTitle) return WorkbookKind.ReportTemplate;

            // 日番号が10日以上並ぶ行があればシフト表とみなす
            if (ExcelHelper.FindDayNumberRow(sheet, scanRows: 60, scanCols: 60, minRun: 10) != null)
                return WorkbookKind.Shift;
        }

        return WorkbookKind.Unknown;
    }

    private static WorkbookKind DetectByFileName(string name)
    {
        if (name.Contains("打刻") || name.Contains("タイムレコーダー") || name.Contains("出席記録"))
            return WorkbookKind.Punch;
        if (name.Contains("シフト") || name.Contains("勤務表"))
            return WorkbookKind.Shift;
        return WorkbookKind.Unknown;
    }

    /// <summary>画面に出すための名称。</summary>
    public static string Label(WorkbookKind kind) => kind switch
    {
        WorkbookKind.Shift => "シフト表",
        WorkbookKind.Punch => "打刻データ",
        WorkbookKind.ReportTemplate => "帳票テンプレート",
        _ => "種類不明"
    };
}
