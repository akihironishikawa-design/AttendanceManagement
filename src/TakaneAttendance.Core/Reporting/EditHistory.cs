using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Reporting;

/// <summary>
/// 修正履歴の1件(統合仕様書 v3.0 第18.1章)。
///
/// 画面でシフトを直したときの「誰が・いつ・何を・どう変えたか」と、
/// その結果 判定がどう変わったかを残す。締めの説明可能性を確保するための記録。
/// </summary>
public sealed class EditHistoryEntry
{
    /// <summary>修正ID(実行内の連番)</summary>
    public required int Id { get; init; }
    public required DateTime EditedAt { get; init; }
    /// <summary>修正者(Windows のユーザー名)</summary>
    public required string EditedBy { get; init; }

    public required string EmployeeNo { get; init; }
    public required string PersonName { get; init; }
    public required DateOnly WorkDate { get; init; }

    /// <summary>修正した項目(勤務区分・予定開始 / 予定終了 / 備考 / 社員情報)</summary>
    public required string Field { get; init; }
    public required string Before { get; init; }
    public required string After { get; init; }

    /// <summary>修正前の主判定</summary>
    public required string JudgementBefore { get; init; }
    /// <summary>修正後の主判定</summary>
    public required string JudgementAfter { get; init; }

    /// <summary>修正理由・申請書確認など</summary>
    public string Note { get; set; } = "";

    /// <summary>判定が変わったか(帳票で目立たせる対象)。</summary>
    public bool JudgementChanged => JudgementBefore != JudgementAfter;

    public string ToLogLine()
        => $"#{Id,-4} {EditedAt:MM/dd HH:mm} {EditedBy,-12} {PersonName,-14} {WorkDate:MM/dd} " +
           $"{Field,-12} 「{Or(Before)}」→「{Or(After)}」  判定 {JudgementBefore}→{JudgementAfter}" +
           (Note.Length > 0 ? $"  備考: {Note}" : "");

    private static string Or(string value) => value.Length > 0 ? value : "(空欄)";
}

/// <summary>
/// 修正履歴の記録先(統合仕様書 v3.0 第18.1章)。
///
/// 画面の編集ごとに1件積む。突合をやり直すと編集自体が破棄されるため、
/// 履歴も同時に消して、画面の内容と履歴が食い違わないようにする。
/// </summary>
public sealed class EditHistory
{
    private readonly List<EditHistoryEntry> _entries = new();
    private int _nextId = 1;

    public IReadOnlyList<EditHistoryEntry> Entries => _entries;
    public int Count => _entries.Count;
    /// <summary>判定が変わった修正の件数。</summary>
    public int JudgementChangedCount => _entries.Count(e => e.JudgementChanged);

    /// <summary>修正者。既定は Windows のユーザー名(仕様書 要確認 Q-10)。</summary>
    public string EditedBy { get; set; } = Environment.UserName;

    public EditHistoryEntry Add(
        string employeeNo, string personName, DateOnly workDate,
        string field, string before, string after,
        string judgementBefore, string judgementAfter, string note = "")
    {
        var entry = new EditHistoryEntry
        {
            Id = _nextId++,
            EditedAt = DateTime.Now,
            EditedBy = EditedBy,
            EmployeeNo = employeeNo,
            PersonName = personName,
            WorkDate = workDate,
            Field = field,
            Before = before,
            After = after,
            JudgementBefore = judgementBefore,
            JudgementAfter = judgementAfter,
            Note = note
        };
        _entries.Add(entry);
        return entry;
    }

    public void Clear()
    {
        _entries.Clear();
        _nextId = 1;
    }

    /// <summary>保留ファイルから復元する(採番は続きから)。</summary>
    public void Restore(IEnumerable<EditHistoryEntry> entries)
    {
        _entries.Clear();
        _entries.AddRange(entries);
        _nextId = _entries.Count == 0 ? 1 : _entries.Max(e => e.Id) + 1;
    }
}
