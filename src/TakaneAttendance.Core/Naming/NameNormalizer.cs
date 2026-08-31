using System.Text;
using System.Globalization;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Naming;

/// <summary>
/// 社員名の正規化と正式氏名への解決を行う。
///
/// 詳細設計書 v2.0 第7章に準拠:
///   1. 前後の半角/全角スペースを除去
///   2. 内部の空白・制御文字を除去
///   3. 別名マスタに一致すれば正式氏名へ変換
///   4. 正式社員名と一致すればそのまま
///   5. いずれにも該当しなければ未解決(NAME_UNRESOLVED)
///
/// 正規化後は完全一致のみ。曖昧一致(あいまい検索)は誤突合の原因になるため行わない。
/// </summary>
public sealed class NameNormalizer
{
    private readonly AliasMaster _alias;
    private readonly EmployeeMaster? _employees;

    public NameNormalizer(AliasMaster alias, EmployeeMaster? employees = null)
    {
        _alias = alias;
        _employees = employees;
    }

    /// <summary>
    /// 表記ゆれを吸収した比較用の文字列にする。
    /// 全角空白・半角空白・タブ・制御文字を除去し、全角英数字は半角へ寄せる。
    /// 氏名の漢字自体は変換しない(異体字は別名マスタで対応する)。
    /// </summary>
    public static string Normalize(string? source)
    {
        if (string.IsNullOrEmpty(source)) return string.Empty;

        // 全角英数字・記号を半角へ(NFKC)。漢字・かなは変化しない。
        var s = source.Normalize(NormalizationForm.FormKC);

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            if (char.IsWhiteSpace(ch)) continue;                 // 半角/全角スペース・タブ・改行
            if (ch == '　') continue;                         // 念のため全角スペース
            if (char.IsControl(ch)) continue;
            var cat = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == UnicodeCategory.Format) continue;          // 異体字セレクタ等
            sb.Append(ch);
        }
        return sb.ToString();
    }

    /// <summary>氏名を解決して PersonRef を作る。解決できない場合 CanonicalName は null。</summary>
    public PersonRef Resolve(string sourceName, string? department = null, string? employeeNo = null)
    {
        var normalized = Normalize(sourceName);
        var canonical = _alias.Resolve(normalized);
        // 突合キーは必ず正規化して持つ(別名マスタの正式氏名と打刻側の氏名で空白の入り方が違うため)
        var key = canonical != null ? Normalize(canonical) : normalized;

        // 従業員マスタの値を補完する。社員番号・部門は入力ファイル側を優先する。
        var employee = _employees?.Find(key);

        return new PersonRef
        {
            SourceName = sourceName?.Trim() ?? string.Empty,
            NormalizedName = normalized,
            CanonicalName = canonical,
            Key = key,
            Department = Prefer(department, employee?.Department),
            EmployeeNo = Prefer(employeeNo, employee?.EmployeeNo),
            Employment = employee?.Employment ?? _employees?.DefaultEmployment ?? EmploymentType.FullTime
        };
    }

    private static string? Prefer(string? primary, string? fallback)
    {
        if (!string.IsNullOrWhiteSpace(primary)) return primary.Trim();
        return string.IsNullOrWhiteSpace(fallback) ? null : fallback.Trim();
    }
}
