using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Parsing;

/// <summary>
/// 複数拠点分の打刻データを1つにまとめる(統合仕様書 v3.0 第10.3章・第12章)。
///
/// 同一社員・同一日付・同一時刻の完全一致は、複数ファイル統合時の重複として1件にする。
/// 時刻が違う打刻は両方を残し、昇順に並べ直したうえで最初と最後を採用できるようにする。
///
/// 打刻の原文は帳票へそのまま出力するため、統合したセルは原文を「+」でつないで保持する。
/// </summary>
public static class PunchMerger
{
    /// <summary>統合の結果。</summary>
    public sealed class MergeResult
    {
        public List<PunchDaily> Punches { get; } = new();
        public List<string> Messages { get; } = new();
        /// <summary>統合で取り除いた重複打刻の件数</summary>
        public int RemovedDuplicates { get; set; }
        /// <summary>複数ファイルにまたがっていた社員日の件数</summary>
        public int MergedCells { get; set; }
    }

    /// <summary>
    /// 読み込み済みの打刻を統合する。
    /// 入力が1ファイル分だけの場合も、同一セル内の重複時刻を取り除く。
    /// </summary>
    public static MergeResult Merge(IEnumerable<IReadOnlyList<PunchDaily>> sources)
    {
        var result = new MergeResult();
        var byKey = new Dictionary<(string Key, DateOnly Date), List<PunchDaily>>();

        foreach (var source in sources)
        {
            foreach (var punch in source)
            {
                var key = (punch.Person.Key, punch.WorkDate);
                if (!byKey.TryGetValue(key, out var list)) byKey[key] = list = new List<PunchDaily>();
                list.Add(punch);
            }
        }

        foreach (var (_, list) in byKey)
        {
            var first = list[0];
            if (list.Count > 1) result.MergedCells++;

            // 同一時刻は1件にまとめ、時刻順に並べ直す(拠点をまたぐと記載順が保証されないため)
            var times = new List<TimeSpan>();
            int seen = 0;
            foreach (var punch in list)
            {
                foreach (var time in punch.Times)
                {
                    seen++;
                    if (times.Contains(time)) continue;
                    times.Add(time);
                }
            }
            times.Sort();
            result.RemovedDuplicates += seen - times.Count;

            result.Punches.Add(new PunchDaily
            {
                Person = first.Person,
                WorkDate = first.WorkDate,
                // 原文は帳票へそのまま出す。複数ファイルにあった場合だけ「+」でつなぐ。
                RawValue = list.Count == 1
                    ? first.RawValue
                    : string.Join(" + ", list.Select(p => p.RawValue).Where(v => v.Length > 0).Distinct()),
                Times = times,
                SourceCell = list.Count == 1
                    ? first.SourceCell
                    : string.Join(" / ", list.Select(p => p.SourceCell))
            });
        }

        if (result.MergedCells > 0 || result.RemovedDuplicates > 0)
            result.Messages.Add(
                $"[打刻統合] {result.MergedCells} 社員日が複数ファイルにありました。" +
                $"完全に一致する重複打刻 {result.RemovedDuplicates} 件を取り除きました。");

        return result;
    }
}
