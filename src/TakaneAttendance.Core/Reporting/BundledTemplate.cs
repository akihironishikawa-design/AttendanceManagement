namespace TakaneAttendance.Core.Reporting;

/// <summary>
/// アプリに同梱している様式ファイルの置き場所。
///
/// 画面でテンプレートを選ばせない帳票(申請書・勤怠管理簿・出勤簿)の様式は、
/// 実行ファイルと同じ場所の templates フォルダに置いて配布する。
/// 差し替えたい場合は、そのフォルダのファイルを入れ替える。
/// </summary>
public static class BundledTemplate
{
    /// <summary>
    /// 様式のパス。実行ファイルの場所から上へたどって探す(開発中の実行にも対応するため)。
    /// 見つからない場合も想定した位置を返す(呼び出し側でファイルの有無を知らせる)。
    /// </summary>
    /// <param name="relativePath">templates からの相対パス(例 templates\出勤簿.xls)</param>
    public static string PathOf(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++, dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(path)) return path;
        }
        return Path.Combine(AppContext.BaseDirectory, relativePath);
    }

    /// <summary>出勤簿の様式(統合仕様書 v3.0 第16章)。</summary>
    public const string AttendanceBookFile = @"templates\出勤簿.xls";

    /// <summary>パート・アルバイト給与計算表の様式(統合仕様書 v3.0 第16章)。</summary>
    public const string PartTimePayrollFile = @"templates\パートアルバイト給与計算表.xlsx";
}
