using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Masters;

/// <summary>判定コードに対して必要になる申請書。</summary>
/// <param name="Code">付録A の判定コード</param>
/// <param name="FormName">申請書の名称</param>
/// <param name="Reason">一覧の「理由」欄に出す説明</param>
public sealed record ApplicationForm(ResultCode Code, string FormName, string Reason);

/// <summary>
/// 申請書マスタ(勤怠締め業務フロー STEP1 ④「申請書を印刷」)。
///
/// 突合の判定結果から、その日に用意すべき申請書を導く。
/// 差異のある日だけでなく、有給・出張のように判定が正常でも
/// 申請書の提出が必要な日を拾えるようにしている。
///
/// XML書式(application_form.xml):
///   &lt;applicationForms&gt;
///     &lt;form code="NO_PUNCH" name="タイムカード修正届出書" reason="打刻漏れ"/&gt;
///   &lt;/applicationForms&gt;
///   同じ code に複数行を書くと、その日に必要な申請書が複数として出る。
/// </summary>
public sealed class ApplicationFormMaster
{
    /// <summary>読み込み時の注意・エラー。</summary>
    public List<string> Messages { get; } = new();

    private readonly List<ApplicationForm> _forms = new();

    public IReadOnlyList<ApplicationForm> All => _forms;
    public int EntryCount => _forms.Count;

    /// <summary>登録されている申請書の名称(重複なし・登録順)。</summary>
    public IReadOnlyList<string> FormNames =>
        _forms.Select(f => f.FormName).Distinct().ToList();

    /// <summary>処理ログに出す1行の要約。</summary>
    public string Summary => _forms.Count == 0
        ? "申請書マスタなし(申請書 確認一覧は出力しません)"
        : $"{FormNames.Count}種類 / {_forms.Count}件の対応 ({string.Join(" , ", FormNames)})";

    public void Register(ResultCode code, string formName, string reason)
    {
        if (string.IsNullOrWhiteSpace(formName)) return;
        _forms.Add(new ApplicationForm(code, formName.Trim(), reason.Trim()));
    }

    /// <summary>1日分の判定コードから、必要な申請書を求める。同じ申請書は1件にまとめる。</summary>
    public IReadOnlyList<ApplicationForm> Resolve(IEnumerable<ResultCode> codes)
    {
        var result = new List<ApplicationForm>();
        foreach (var code in codes)
        {
            foreach (var form in _forms.Where(f => f.Code == code))
            {
                // 同じ日に同じ申請書が二重に出ないようにする
                if (result.Any(r => r.FormName == form.FormName)) continue;
                result.Add(form);
            }
        }
        return result;
    }

    public static ApplicationFormMaster Load(string? xmlPath)
    {
        var m = new ApplicationFormMaster();
        var root = MasterXml.LoadRoot(xmlPath, "申請書マスタ", m.Messages);
        if (root == null) return m;

        foreach (var e in root.Elements("form"))
        {
            var codeText = e.Attr("code");
            var name = e.Attr("name");
            if (codeText.Length == 0 || name.Length == 0) continue;

            if (!TryParseCode(codeText, out var code))
            {
                m.Messages.Add($"[W-MS-010] 申請書マスタの判定コード '{codeText}' は認識できません。この行は無視します。");
                continue;
            }
            m.Register(code, name, e.Attr("reason"));
        }
        return m;
    }

    /// <summary>付録A のコード名(NO_PUNCH など)から判定コードを引く。</summary>
    private static bool TryParseCode(string text, out ResultCode code)
    {
        var target = text.Trim();
        foreach (ResultCode value in Enum.GetValues<ResultCode>())
        {
            if (!string.Equals(ResultCodeInfo.CodeName(value), target, StringComparison.OrdinalIgnoreCase)) continue;
            code = value;
            return true;
        }
        code = ResultCode.Normal;
        return false;
    }
}
