using System.Globalization;
using System.Text.RegularExpressions;

namespace TakaneAttendance.Core.Parsing;

/// <summary>
/// セルに入っている時刻文字列の扱い。
///
/// 打刻は「06:4917:07」のように出勤と退勤が連結された原文で入るため、
/// 「そのセルが時刻だけで構成されているか」を判定してから時刻を取り出す。
/// 勤務区分(公・有 など)を時刻として誤解釈しないための共通処理。
/// </summary>
public static class TimeText
{
    private static readonly Regex TimePattern = new(@"\d{1,2}:\d{2}", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <summary>
    /// 時刻だけで構成されたセルから、時刻を書かれている順に取り出す。
    /// 時刻以外の文字が混ざっている場合は空を返す(勤務区分などを時刻として扱わないため)。
    /// </summary>
    public static IReadOnlyList<string> Extract(string? text)
    {
        var s = text ?? "";
        if (s.Length == 0) return Array.Empty<string>();

        var matches = TimePattern.Matches(s);
        if (matches.Count == 0) return Array.Empty<string>();
        if (string.Concat(matches.Select(m => m.Value)) != Whitespace.Replace(s, ""))
            return Array.Empty<string>();

        return matches.Select(m => m.Value).ToArray();
    }

    /// <summary><see cref="Extract"/> の結果を TimeSpan にしたもの。</summary>
    public static IReadOnlyList<TimeSpan> ExtractTimes(string? text)
    {
        var list = new List<TimeSpan>();
        foreach (var t in Extract(text))
            if (TryParse(t, out var value)) list.Add(value);
        return list;
    }

    /// <summary>「7:30」「0730」「730」を時刻として読む。日をまたぐ勤務があるため 24 時以降も許容する。</summary>
    public static bool TryParse(string? text, out TimeSpan value)
    {
        value = default;
        var s = Whitespace.Replace(text ?? "", "");
        if (s.Length == 0) return false;

        int hour, minute;
        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            if (parts.Length != 2) return false;
            if (!int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out hour)) return false;
            if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out minute)) return false;
        }
        else
        {
            if (s.Length is < 3 or > 4) return false;
            if (!int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)) return false;
            hour = n / 100;
            minute = n % 100;
        }

        if (hour is < 0 or > 47 || minute is < 0 or > 59) return false;
        value = new TimeSpan(hour, minute, 0);
        return true;
    }

    /// <summary>打刻は「07:30」(2桁)、シフトは「7:30」(1桁)。それぞれの元ファイルの表記に合わせる。</summary>
    public static string Format(TimeSpan time, bool padHours)
    {
        int hour = (int)time.TotalHours;
        return padHours ? $"{hour:00}:{time.Minutes:00}" : $"{hour}:{time.Minutes:00}";
    }
}
