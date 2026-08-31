using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Parsing;
using TakaneAttendance.Core.Reporting;

namespace TakaneAttendance.Wpf.ViewModels;

/// <summary>
/// 出席記録レポート画面の1行(社員1名)。
///
/// シフト表の1人1行を基準にし、その上に打刻データを重ねて表示する。
/// 帳票へはシフト行・打刻行の2行で書き出すが、画面では1行にまとめて日ごとに縦に並べる。
/// </summary>
public sealed class ReportRow : ObservableObject
{
    private readonly ReportEmployeeBlock _block;
    private readonly DayCell[] _days;

    public ReportRow(ReportEmployeeBlock block, int order, ReportSheet sheet, ReportJudge? judge)
    {
        _block = block;
        Order = order;

        _days = new DayCell[sheet.DayCount];
        for (int i = 0; i < sheet.DayCount; i++)
            _days[i] = new DayCell(this, block, i, new DateOnly(sheet.Year, sheet.Month, i + 1), judge);
    }

    /// <summary>セルが編集されたときに発火する(件数表示の更新用)。</summary>
    public event EventHandler? CellChanged;
    internal void RaiseCellChanged() => CellChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>並び順(シフト表の記載順)。1行おきに背景色を変えて読みやすくする。</summary>
    public int Order { get; }
    public bool IsAlternate => Order % 2 == 1;

    /// <summary>固定列に出す No.(1始まり)。仕様書 v3.0 第8.1章。</summary>
    public string RowNumberText => (Order + 1).ToString();

    /// <summary>固定列に出す部署。氏名の下に括弧書きで並べる。</summary>
    public string DepartmentText => Department.Length > 0 ? $"({Department})" : "";

    /// <summary>突合に使った社員情報(セルの再判定に使う)。</summary>
    public PersonRef? Person => _block.Person;

    public string EmployeeNo
    {
        get => _block.EmployeeNo;
        set => SetMeta(v => _block.EmployeeNo = v, _block.EmployeeNo, value);
    }

    public string Name
    {
        get => _block.Name;
        set => SetMeta(v => _block.Name = v, _block.Name, value);
    }

    public string Department
    {
        get => _block.Department;
        set => SetMeta(v => _block.Department = v, _block.Department, value);
    }

    /// <summary>日(0始まり = 1日)ごとのセル。DataGrid からは "[0].ShiftText" のようなパスで束縛する。</summary>
    public DayCell this[int index] => _days[index];

    public int EditedCount => _block.EditedCellCount;

    // ---- 絞り込み(仕様書 v3.0 第8.2章) ----

    /// <summary>採用打刻が0件の日を含むか(「打刻データなしのみ表示」)。</summary>
    public bool HasNoPunchDay => _days.Any(d => d.Cell.Label == "打刻漏れ");

    /// <summary>
    /// 色の付いた日を含むか(「色付きセルのみ表示」)。
    /// 時間外は正常と同じ白のため、ここには含めない。
    /// </summary>
    public bool HasColoredDay => _days.Any(d => d.Judgement is not (Judgement.Normal or Judgement.Overtime));
    public int AttentionCount => _block.AttentionCount;

    private void SetMeta(Action<string> assign, string current, string? value)
    {
        var v = (value ?? "").Trim();
        if (current == v) return;

        assign(v);
        _block.MetaEdited = true;
        OnPropertyChanged(nameof(EmployeeNo));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Department));
        RaiseCellChanged();
    }
}

/// <summary>1人1日分のセル。シフト(予定)と打刻(実績)、その矛盾の判定を持つ。</summary>
public sealed class DayCell : ObservableObject
{
    private readonly ReportRow _row;
    private readonly ReportEmployeeBlock _block;
    private readonly int _index;
    private readonly ReportJudge? _judge;

    public DayCell(ReportRow row, ReportEmployeeBlock block, int index, DateOnly date, ReportJudge? judge)
    {
        _row = row;
        _block = block;
        _index = index;
        _judge = judge;
        Date = date;
    }

    public DateOnly Date { get; }

    /// <summary>帳票のシフト行に書き出す値(勤務区分の文字値、または予定開始時刻)。</summary>
    public string ShiftValue => _block.Shift[_index];
    /// <summary>帳票の打刻行に書き出す値(タイムレコーダーの原文)。</summary>
    public string PunchValue => _block.Punch[_index];

    // ---- 画面表示(1セルを3段に分けて、上=シフト / 中=出勤 / 下=退勤) ----

    public string ShiftText => ShiftValue;

    public string PunchInText
    {
        get
        {
            var times = TimeText.Extract(PunchValue);
            // 時刻として読めない値は、取りこぼしが分かるよう原文のまま出す
            if (times.Count == 0) return PunchValue;
            return times[0];
        }
    }

    public string PunchOutText
    {
        get
        {
            var times = TimeText.Extract(PunchValue);
            return times.Count > 1 ? times[^1] : "";
        }
    }

    // ---- 判定(矛盾) ----

    public CellJudgement Cell => _block.Judgements[_index];

    /// <summary>予定終了時刻。早退・時間外の判定に使う(帳票には出力しない)。</summary>
    public string PlannedEndValue => _block.PlannedEnd[_index];

    /// <summary>備考(修正理由・申請書の確認結果)。</summary>
    public string NoteValue => _block.Note[_index];
    /// <summary>主判定。日付セルの背景色に使う(仕様書 v3.0 第15.1章)。</summary>
    public Judgement Judgement => Cell.Judgement;
    /// <summary>判定結果行の文言(要確認 / 打刻漏れ / 遅 / 早 / 早出 / 時間外 / -)。</summary>
    public string JudgementLabel => Cell.Label;
    public string Note => Cell.Note;
    public bool Edited => _block.ShiftEdited[_index] || _block.PunchEdited[_index];

    /// <summary>セルにカーソルを合わせたときに出す説明。</summary>
    public string ToolTipText
    {
        get
        {
            var lines = new List<string>
            {
                $"{Date:yyyy/MM/dd}({DayOfWeekText})  {_block.Name}",
                $"シフト : {(ShiftValue.Length > 0 ? ShiftValue : "(なし)")}",
                $"打刻   : {(PunchValue.Length > 0 ? PunchValue : "(なし)")}",
                $"判定   : {(Note.Length > 0 ? Note : "矛盾なし")}"
            };
            if (Edited) lines.Add("※ 画面で編集したセルです");
            return string.Join("\n", lines);
        }
    }

    private string DayOfWeekText => Date.DayOfWeek switch
    {
        DayOfWeek.Sunday => "日", DayOfWeek.Monday => "月", DayOfWeek.Tuesday => "火",
        DayOfWeek.Wednesday => "水", DayOfWeek.Thursday => "木", DayOfWeek.Friday => "金", _ => "土"
    };

    /// <summary>
    /// 編集内容を反映し、その場で矛盾を判定し直す。
    ///
    /// 打刻(実績)は原則そのままだが、申請書の提出を確認した場合だけ直せる
    /// (セルの編集画面の「申請書提出確認済み」。仕様書 v3.0 第8.3章)。
    /// </summary>
    /// <param name="punchValue">打刻(実績)。変更しない場合は今の値をそのまま渡す。</param>
    /// <param name="history">修正履歴の記録先(仕様書 第18.1章)。</param>
    public void Apply(string shiftValue, string plannedEndValue, string noteValue,
                      string punchValue, EditHistory? history = null)
    {
        var shift = (shiftValue ?? "").Trim();
        var plannedEnd = (plannedEndValue ?? "").Trim();
        var note = (noteValue ?? "").Trim();
        var punch = (punchValue ?? "").Trim();

        if (shift == ShiftValue && plannedEnd == PlannedEndValue && note == NoteValue && punch == PunchValue) return;

        var judgementBefore = Cell.Label;
        var changes = new List<(string Field, string Before, string After)>();

        if (shift != ShiftValue)
        {
            changes.Add(("シフト", ShiftValue, shift));
            _block.Shift[_index] = shift;
            _block.ShiftEdited[_index] = true;
        }
        if (plannedEnd != PlannedEndValue)
        {
            changes.Add(("予定終了", PlannedEndValue, plannedEnd));
            _block.PlannedEnd[_index] = plannedEnd;
            _block.ShiftEdited[_index] = true;
        }
        if (note != NoteValue)
        {
            changes.Add(("備考", NoteValue, note));
            _block.Note[_index] = note;
        }
        if (punch != PunchValue)
        {
            changes.Add(("打刻", PunchValue, punch));
            _block.Punch[_index] = punch;
            _block.PunchEdited[_index] = true;
        }

        // 突合時と同じルールで判定し直す(編集後に古い判定が残らないようにする)
        if (_judge != null && _block.Person is { } person)
            _block.Judgements[_index] = _judge.Evaluate(person, Date, shift, punch, plannedEnd);

        // 誰がいつ何をどう変え、判定がどう変わったかを残す
        if (history != null)
            foreach (var (field, before, after) in changes)
                history.Add(_block.EmployeeNo, _block.Name, Date, field, before, after,
                            judgementBefore, Cell.Label, note);

        foreach (var name in new[] { nameof(ShiftText), nameof(PunchInText), nameof(PunchOutText),
                                     nameof(ShiftValue), nameof(PunchValue), nameof(PlannedEndValue),
                                     nameof(NoteValue), nameof(Judgement), nameof(JudgementLabel),
                                     nameof(Note), nameof(Edited), nameof(ToolTipText) })
            OnPropertyChanged(name);

        _row.RaiseCellChanged();
    }
}
