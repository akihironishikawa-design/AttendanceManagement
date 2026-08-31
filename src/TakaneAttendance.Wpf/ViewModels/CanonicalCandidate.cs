using TakaneAttendance.Core.Naming;

namespace TakaneAttendance.Wpf.ViewModels;

/// <summary>
/// 別名マスタへ登録するときに選ぶ「正式氏名」の候補。
/// 打刻データ側の社員(作業番号を持つ = 正式氏名として信頼できる)から作る。
/// </summary>
/// <param name="Name">正式氏名</param>
/// <param name="EmployeeNo">作業番号</param>
/// <param name="Department">部門</param>
/// <param name="MatchesSurname">未解決の氏名と姓が一致するか(候補の並べ替えに使う)</param>
public sealed record CanonicalCandidate(string Name, string EmployeeNo, string Department, bool MatchesSurname = false)
{
    /// <summary>絞り込み用。氏名は空白の入り方が違っても拾えるよう、正規化した形でも比べる。</summary>
    public bool Contains(string text) =>
        Name.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        NameNormalizer.Normalize(Name).Contains(NameNormalizer.Normalize(text), StringComparison.OrdinalIgnoreCase) ||
        EmployeeNo.Contains(text, StringComparison.OrdinalIgnoreCase) ||
        Department.Contains(text, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 未解決の氏名に対して、姓が一致する候補を先頭に並べ替える。
    /// シフト表の「加藤 主任」に対して打刻データの「加藤 十蔵」を最初に出すのが狙い。
    /// </summary>
    public static IReadOnlyList<CanonicalCandidate> Rank(IEnumerable<CanonicalCandidate> candidates, string sourceName)
    {
        var surname = Surname(sourceName);

        return candidates
            .Select(c => c with { MatchesSurname = surname.Length > 0 && Surname(c.Name) == surname })
            .OrderByDescending(c => c.MatchesSurname)
            .ThenBy(c => c.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>
    /// 氏名から姓を取り出す。「加藤 主任」のように姓と役職・名が空白で区切られている前提。
    /// 空白が無い場合は先頭2文字を姓とみなす(日本語の姓の多くは2文字のため)。
    /// </summary>
    private static string Surname(string name)
    {
        var text = (name ?? "").Trim();
        if (text.Length == 0) return "";

        int space = text.IndexOfAny(new[] { ' ', '　', '\t' });
        if (space > 0) return text[..space];
        return text.Length >= 2 ? text[..2] : text;
    }
}
