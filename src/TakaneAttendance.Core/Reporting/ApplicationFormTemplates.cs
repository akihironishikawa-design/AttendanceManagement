namespace TakaneAttendance.Core.Reporting;

/// <summary>
/// 申請書の様式ファイルの置き場所。
///
/// 様式(Materials でお預かりした format_*.xls)はアプリに同梱しており、
/// 画面でテンプレートを指定させない。差し替えたい場合は、実行ファイルと同じ場所の
/// 「templates\申請書」フォルダのファイルを入れ替える。
/// </summary>
public static class ApplicationFormTemplates
{
    /// <summary>同梱している様式のフォルダ名。</summary>
    public const string FolderName = @"templates\申請書";

    /// <summary>様式のパス。見つからない場合も想定した位置を返す(呼び出し側でファイルの有無を出す)。</summary>
    public static string PathOf(ApplicationFormKind kind)
        => BundledTemplate.PathOf(Path.Combine(FolderName,
               ApplicationFormKinds.NameOf(kind) + ApplicationFormKinds.ExtensionOf(kind)));

    /// <summary>同梱の様式がそろっているか。</summary>
    public static bool AllExist() => ApplicationFormKinds.All.All(k => File.Exists(PathOf(k)));
}
