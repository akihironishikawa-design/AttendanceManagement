using System.Globalization;
using TakaneAttendance.Core.Matching;

namespace TakaneAttendance.Core.Masters;

/// <summary>
/// 判定閾値マスタ(統合仕様書 v3.0 第17章)。
///
/// 「早出30分」「時間外30分」「正社員拘束9時間30分」をコードから分離し、
/// 運用が変わったときに本体を直さずに合わせられるようにする(仕様書 第20章 保守性)。
///
/// XML書式(judgement_rule.xml):
///   &lt;judgementRule earlyInMinutes="30" overtimeMinutes="30"
///                  fullTimeSpanMinutes="570" toleranceMinutes="0"/&gt;
/// </summary>
public sealed class JudgementRuleMaster
{
    /// <summary>読み込み時の注意・エラー。</summary>
    public List<string> Messages { get; } = new();

    /// <summary>早出のしきい値(分)。既定30分。29分は対象外・30分は対象。</summary>
    public int EarlyInMinutes { get; set; } = 30;
    /// <summary>時間外のしきい値(分)。既定30分。</summary>
    public int OvertimeMinutes { get; set; } = 30;
    /// <summary>正社員の拘束時間(分)。既定570分(9時間30分)。休憩1時間30分を含む。</summary>
    public int FullTimeSpanMinutes { get; set; } = 570;
    /// <summary>遅刻・早退の許容(分)。既定0分(1分超過から遅刻)。</summary>
    public int ToleranceMinutes { get; set; }

    /// <summary>処理ログに出す1行の要約。</summary>
    public string Summary =>
        $"早出{EarlyInMinutes}分 / 時間外{OvertimeMinutes}分 / " +
        $"正社員拘束{FullTimeSpanMinutes / 60}時間{FullTimeSpanMinutes % 60:00}分 / 遅刻早退の許容{ToleranceMinutes}分";

    /// <summary>読み込んだしきい値を突合の設定へ反映する。</summary>
    public void ApplyTo(MatchingOptions options)
    {
        options.EarlyInMinutes = EarlyInMinutes;
        options.OvertimeMinutes = OvertimeMinutes;
        options.FullTimeSpanMinutes = FullTimeSpanMinutes;
        options.ToleranceMinutes = ToleranceMinutes;
    }

    public static JudgementRuleMaster Load(string? xmlPath)
    {
        var m = new JudgementRuleMaster();
        var root = MasterXml.LoadRoot(xmlPath, "判定閾値マスタ", m.Messages);
        if (root == null) return m;

        m.EarlyInMinutes      = ReadMinutes(root.Attr("earlyInMinutes"),      "earlyInMinutes",      m.EarlyInMinutes,      m.Messages);
        m.OvertimeMinutes     = ReadMinutes(root.Attr("overtimeMinutes"),     "overtimeMinutes",     m.OvertimeMinutes,     m.Messages);
        m.FullTimeSpanMinutes = ReadMinutes(root.Attr("fullTimeSpanMinutes"), "fullTimeSpanMinutes", m.FullTimeSpanMinutes, m.Messages);
        m.ToleranceMinutes    = ReadMinutes(root.Attr("toleranceMinutes"),    "toleranceMinutes",    m.ToleranceMinutes,    m.Messages);
        return m;
    }

    private static int ReadMinutes(string text, string attributeName, int fallback, List<string> messages)
    {
        if (text.Length == 0) return fallback;
        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
            return value;

        messages.Add($"[W-MS-009] 判定閾値マスタの {attributeName} '{text}' を分として読めません。{fallback}分で続行します。");
        return fallback;
    }
}
