using System.Globalization;
using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Masters;

/// <summary>打刻を丸める方向。</summary>
public enum RoundingMode
{
    /// <summary>四捨五入(単位の半分ちょうどは切り上げ)</summary>
    Nearest,
    Up,
    Down
}

/// <summary>
/// 休憩時間の帯。拘束時間が上限に収まる場合に <see cref="BreakMinutes"/> を休憩とする。
/// </summary>
/// <param name="UpperHours">上限(時間)。null なら上限なし(それ以上すべて)。</param>
/// <param name="IncludeUpper">上限ちょうどを含むか(true = 以内 / false = 未満)。</param>
public sealed record BreakBand(double? UpperHours, bool IncludeUpper, int BreakMinutes)
{
    public bool Matches(double spanHours)
    {
        if (UpperHours is not { } limit) return true;
        return IncludeUpper ? spanHours <= limit : spanHours < limit;
    }

    /// <summary>画面・処理ログに出す説明(例「6時間以内 → 休憩15分」)。</summary>
    public string Describe()
    {
        var range = UpperHours is not { } limit
            ? "それ以上"
            : $"{FormatHours(limit)}時間{(IncludeUpper ? "以内" : "未満")}";
        return $"{range} → 休憩{BreakMinutes}分";
    }

    private static string FormatHours(double h)
        => h == Math.Floor(h) ? ((int)h).ToString() : h.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>
/// 休憩時間の自動計算と打刻の丸め(統合仕様書 v3.0 第14章)。
///
/// 出退勤の打刻を単位時間で丸めたうえで、拘束時間の長さから休憩時間を決める。
///   6時間以内        → 休憩15分
///   6時間超〜8時間以内 → 休憩45分
///   8時間超          → 休憩1時間30分
///
/// 帯・丸めの単位・丸めの方向はすべて break_rule.xml で変更できるようにしてある。
/// とくに丸めの方向は仕様書 Q-01 で未確定のため、設定値として外出ししている。
///
/// XML書式(break_rule.xml):
///   &lt;breakRule unitMinutes="15" inRounding="up" outRounding="down"&gt;
///     &lt;band upToHours="6" breakMinutes="15"/&gt;
///     &lt;band upToHours="8" breakMinutes="45"/&gt;
///     &lt;band               breakMinutes="90"/&gt;
///   &lt;/breakRule&gt;
/// </summary>
public sealed class BreakRuleMaster
{
    /// <summary>読み込み時の注意・エラー。</summary>
    public List<string> Messages { get; } = new();

    /// <summary>丸めの単位(分)。0 以下なら丸めない。</summary>
    public int UnitMinutes { get; set; } = 15;

    // 既定は「出勤は切り上げ・退勤は切り捨て」。
    // 丸めの方向は仕様書 v3.0 Q-01 で未確定のため、決定後に切り替えられるようにしている。
    public RoundingMode InRounding { get; set; } = RoundingMode.Up;
    public RoundingMode OutRounding { get; set; } = RoundingMode.Down;

    /// <summary>上から順に判定する帯。</summary>
    public List<BreakBand> Bands { get; } = new();

    /// <summary>処理ログに出す1行の要約。</summary>
    public string Summary =>
        $"{UnitMinutes}分丸め(出勤:{Label(InRounding)} / 退勤:{Label(OutRounding)}) " +
        string.Join(" , ", Bands.Select(b => b.Describe()));

    private static string Label(RoundingMode m) => m switch
    {
        RoundingMode.Up => "切り上げ", RoundingMode.Down => "切り捨て", _ => "四捨五入"
    };

    /// <summary>
    /// 1日分の勤務時間を求める。
    /// 退勤が出勤より前(日跨ぎ・打刻の誤り)の場合は計算しない。
    /// </summary>
    public WorkTime? Calculate(TimeSpan actualIn, TimeSpan actualOut)
    {
        var roundedIn = Round(actualIn, InRounding);
        var roundedOut = Round(actualOut, OutRounding);
        if (roundedOut <= roundedIn) return null;

        var span = roundedOut - roundedIn;
        int breakMinutes = ResolveBreakMinutes(span.TotalHours);

        return new WorkTime
        {
            RoundedIn = roundedIn,
            RoundedOut = roundedOut,
            SpanMinutes = (int)span.TotalMinutes,
            BreakMinutes = breakMinutes,
            AppliedBand = Bands.FirstOrDefault(b => b.Matches(span.TotalHours))?.Describe() ?? ""
        };
    }

    /// <summary>拘束時間(時間)に対する休憩時間(分)。当てはまる帯が無ければ 0。</summary>
    public int ResolveBreakMinutes(double spanHours)
        => Bands.FirstOrDefault(b => b.Matches(spanHours))?.BreakMinutes ?? 0;

    public TimeSpan Round(TimeSpan value, RoundingMode mode)
    {
        if (UnitMinutes <= 0) return value;

        double units = value.TotalMinutes / UnitMinutes;
        double rounded = mode switch
        {
            RoundingMode.Up => Math.Ceiling(units),
            RoundingMode.Down => Math.Floor(units),
            // Math.Round は銀行家丸めのため、日本の四捨五入に合わせて明示的に計算する
            _ => Math.Floor(units + 0.5)
        };
        return TimeSpan.FromMinutes(rounded * UnitMinutes);
    }

    /// <summary>マスタが無い場合に使う既定のルール(仕様書 v3.0 第14.3章)。</summary>
    public static BreakRuleMaster CreateDefault()
    {
        var m = new BreakRuleMaster();
        m.Bands.Add(new BreakBand(6, IncludeUpper: true, 15));   // 6時間以内
        m.Bands.Add(new BreakBand(8, IncludeUpper: true, 45));   // 6時間超〜8時間以内
        m.Bands.Add(new BreakBand(null, IncludeUpper: true, 90)); // 8時間超
        return m;
    }

    public static BreakRuleMaster Load(string? xmlPath)
    {
        var m = new BreakRuleMaster();
        var root = MasterXml.LoadRoot(xmlPath, "休憩ルールマスタ", m.Messages);
        if (root == null)
        {
            var fallback = CreateDefault();
            fallback.Messages.AddRange(m.Messages);
            return fallback;
        }

        var unitText = root.Attr("unitMinutes");
        if (unitText.Length > 0)
        {
            if (int.TryParse(unitText, out var unit) && unit >= 0) m.UnitMinutes = unit;
            else m.Messages.Add($"[W-MS-006] 休憩ルールマスタの unitMinutes '{unitText}' を分として読めません。15分で続行します。");
        }

        // 属性が無い場合は既定(出勤=切り上げ / 退勤=切り捨て)のままにする
        m.InRounding = ParseRounding(root.Attr("inRounding"), "inRounding", m.InRounding, m.Messages);
        m.OutRounding = ParseRounding(root.Attr("outRounding"), "outRounding", m.OutRounding, m.Messages);

        foreach (var e in root.Elements("band"))
        {
            var minutesText = e.Attr("breakMinutes");
            if (!int.TryParse(minutesText, out var minutes) || minutes < 0)
            {
                m.Messages.Add($"[W-MS-007] 休憩ルールの breakMinutes '{minutesText}' を分として読めません。この行は無視します。");
                continue;
            }

            var upTo = e.Attr("upToHours");     // 以内(境界を含む)
            var under = e.Attr("underHours");   // 未満(境界を含まない)

            if (upTo.Length > 0 && under.Length > 0)
            {
                m.Messages.Add("[W-MS-007] 休憩ルールの band に upToHours と underHours の両方があります。upToHours を使います。");
                under = "";
            }

            if (upTo.Length == 0 && under.Length == 0)
            {
                m.Bands.Add(new BreakBand(null, true, minutes));   // 上限なし
                continue;
            }

            var text = upTo.Length > 0 ? upTo : under;
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours))
            {
                m.Messages.Add($"[W-MS-007] 休憩ルールの上限 '{text}' を時間として読めません。この行は無視します。");
                continue;
            }
            m.Bands.Add(new BreakBand(hours, IncludeUpper: upTo.Length > 0, minutes));
        }

        if (m.Bands.Count == 0)
        {
            m.Messages.Add("[W-MS-007] 休憩ルールマスタに有効な band がありません。既定のルールで続行します。");
            foreach (var b in CreateDefault().Bands) m.Bands.Add(b);
        }

        return m;
    }

    private static RoundingMode ParseRounding(string text, string attributeName, RoundingMode fallback, List<string> messages)
    {
        if (text.Length == 0) return fallback;
        switch (text.ToLowerInvariant())
        {
            case "nearest": return RoundingMode.Nearest;
            case "up": return RoundingMode.Up;
            case "down": return RoundingMode.Down;
            default:
                messages.Add($"[W-MS-006] 休憩ルールマスタの {attributeName} '{text}' は nearest / up / down のいずれかです。" +
                             $"既定({Label(fallback)})で続行します。");
                return fallback;
        }
    }
}
