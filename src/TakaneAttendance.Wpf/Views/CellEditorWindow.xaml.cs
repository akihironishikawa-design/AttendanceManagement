using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Parsing;
using TakaneAttendance.Wpf.ViewModels;

namespace TakaneAttendance.Wpf.Views;

/// <summary>
/// 出席記録レポートのセル(1人1日)を編集するポップアップ。
///
/// シフト(予定)は勤務区分か開始時刻のどちらか一方を選ぶ。
/// 予定終了時刻と備考も直せる。打刻(実績)は MB20 の原値のため読取専用で表示するだけ
/// (仕様書 v3.0 第8.3章)。
/// 確定すると、帳票へ出力する原文の形(区分の文字値、または時刻の連結表記)に組み立てる。
/// </summary>
public partial class CellEditorWindow : Window
{
    /// <summary>区分リストの1項目。</summary>
    public sealed record CodeChoice(string Value, string Label, string Detail);

    private bool _suspend;
    private bool _punchUnlocked;

    /// <summary>OK で確定したときのシフト(予定)の値。</summary>
    public string ShiftResult { get; private set; } = "";
    /// <summary>OK で確定したときの予定終了時刻。空なら雇用区分・マスタから補完する。</summary>
    public string PlannedEndResult { get; private set; } = "";
    /// <summary>OK で確定したときの備考。</summary>
    public string NoteResult { get; private set; } = "";
    /// <summary>OK で確定したときの打刻(実績)。申請書の提出を確認していない場合は元のまま。</summary>
    public string PunchResult { get; private set; } = "";

    /// <summary>申請書の提出を確認したときに備考へ残す文言。</summary>
    private const string ConfirmedNote = "申請書提出確認済み";

    public CellEditorWindow(DayCell cell, string personName, IReadOnlyList<ShiftTypeEntry> shiftTypes)
    {
        InitializeComponent();

        TitleText.Text = $"{cell.Date:yyyy/MM/dd}({DayOfWeekText(cell.Date)})  {personName}";
        CurrentText.Text =
            $"現在 — シフト: {Or(cell.ShiftValue, "(なし)")} / 判定: {cell.Cell.Label}";

        if (cell.Note.Length > 0)
        {
            NoteText.Text = $"判定: {cell.Note}";
            NoteBox.Visibility = Visibility.Visible;
        }

        // 全打刻を出して、3件以上ある日も中身が分かるようにする。
        var punches = TimeText.Extract(cell.PunchValue);
        PunchText.Text = cell.PunchValue.Length == 0 ? "打刻なし" : string.Join("  ", punches);
        PunchResult = cell.PunchValue;

        EndHintText.Text = cell.PlannedEndValue.Length > 0
            ? "空欄にすると、雇用区分と勤務パターンから自動で補完します。"
            : "未入力です。正社員は「予定開始 + 9時間30分」、パート・アルバイトは勤務パターンマスタで補完します。";

        NoteInput.Text = cell.NoteValue;

        BuildTimeLists();
        BuildCodeList(cell.ShiftValue, shiftTypes);
        Prefill(cell.ShiftValue, cell.PlannedEndValue);
        PrefillPunch(punches);
    }

    private static string Or(string value, string fallback) => value.Length > 0 ? value : fallback;

    private static string DayOfWeekText(DateOnly d) => d.DayOfWeek switch
    {
        DayOfWeek.Sunday => "日", DayOfWeek.Monday => "月", DayOfWeek.Tuesday => "火",
        DayOfWeek.Wednesday => "水", DayOfWeek.Thursday => "木", DayOfWeek.Friday => "金", _ => "土"
    };

    /// <summary>時・分の選択肢。手打ちを減らすため一覧から選べるようにする。</summary>
    private void BuildTimeLists()
    {
        // 先頭の空欄は「その時刻を消す」ための選択肢
        var hours = new List<string> { "" };
        for (int h = 0; h <= 23; h++) hours.Add(h.ToString());

        var minutes = new List<string> { "" };
        for (int m = 0; m < 60; m += 5) minutes.Add($"{m:00}");

        foreach (var box in new[] { StartHour, EndHour, InHour, OutHour }) box.ItemsSource = new List<string>(hours);
        foreach (var box in new[] { StartMinute, EndMinute, InMinute, OutMinute }) box.ItemsSource = new List<string>(minutes);

        // シフトの開始時刻を触ったら勤務区分の選択は外す(どちらか一方のため)
        foreach (var box in new[] { StartHour, StartMinute })
            box.AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(StartTime_TextChanged));
    }

    /// <summary>区分の一覧を作る。時刻セル扱いの区分(Work)は時刻入力側で扱うため載せない。</summary>
    private void BuildCodeList(string shiftValue, IReadOnlyList<ShiftTypeEntry> shiftTypes)
    {
        var choices = new List<CodeChoice> { new("", "(なし)", "勤務区分を使わない") };
        foreach (var t in shiftTypes)
        {
            if (t.Kind == ShiftKind.Work) continue;
            choices.Add(new CodeChoice(t.Code, t.Code, t.DisplayName));
        }

        // マスタに無い値が入っている場合でも失わないよう、その値を一覧に足しておく
        if (shiftValue.Length > 0 && TimeText.Extract(shiftValue).Count == 0 &&
            !choices.Any(c => c.Value == shiftValue))
            choices.Insert(1, new CodeChoice(shiftValue, shiftValue, "現在の値(マスタ未登録)"));

        CodeList.ItemsSource = choices;
    }

    private void Prefill(string shiftValue, string plannedEndValue)
    {
        _suspend = true;
        try
        {
            var shiftTimes = TimeText.Extract(shiftValue);
            if (shiftTimes.Count > 0)
            {
                SetTime(StartHour, StartMinute, shiftTimes[0]);
                CodeList.SelectedIndex = -1;
            }
            else
            {
                var choices = (List<CodeChoice>)CodeList.ItemsSource;
                CodeList.SelectedItem = choices.FirstOrDefault(c => c.Value == shiftValue) ?? choices[0];
            }

            var endTimes = TimeText.Extract(plannedEndValue);
            SetTime(EndHour, EndMinute, endTimes.Count > 0 ? endTimes[0] : "");
        }
        finally { _suspend = false; }
    }

    /// <summary>打刻(実績)の欄に、いまの出勤・退勤を入れる。3件以上ある日は最初と最後を使う。</summary>
    private void PrefillPunch(IReadOnlyList<string> punches)
    {
        _suspend = true;
        try
        {
            SetTime(InHour, InMinute, punches.Count > 0 ? punches[0] : "");
            SetTime(OutHour, OutMinute, punches.Count > 1 ? punches[^1] : "");
        }
        finally { _suspend = false; }
    }

    /// <summary>「7:30」を 時・分 のそれぞれの欄に入れる。</summary>
    private static void SetTime(ComboBox hourBox, ComboBox minuteBox, string time)
    {
        var parts = time.Split(':');
        if (parts.Length != 2)
        {
            hourBox.Text = "";
            minuteBox.Text = "";
            return;
        }
        hourBox.Text = int.TryParse(parts[0], out var h) ? h.ToString() : parts[0];
        minuteBox.Text = parts[1];
    }

    // シフトは「勤務区分」と「開始時刻」のどちらか一方。触ったらもう一方を消す。
    private void CodeList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suspend || CodeList.SelectedItem is not CodeChoice choice || choice.Value.Length == 0) return;
        _suspend = true;
        StartHour.Text = "";
        StartMinute.Text = "";
        _suspend = false;
    }

    private void StartTime_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suspend) return;
        if (sender is ComboBox { Text.Length: 0 }) return;
        _suspend = true;
        CodeList.SelectedIndex = -1;
        _suspend = false;
    }

    private void CodeList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (CodeList.SelectedItem is CodeChoice) Ok_Click(sender, e);
    }

    /// <summary>
    /// 申請書の提出を確認したら、打刻(実績)を直せるようにする。
    ///
    /// 打刻はタイムレコーダーの原値のため、通常は変更しない。
    /// タイムカード修正届出書などの提出を確認した日だけ、ここから実績を直す
    /// (仕様書 v3.0 第8.3章)。
    /// </summary>
    private void UnlockPunch_Click(object sender, RoutedEventArgs e)
    {
        _punchUnlocked = true;

        foreach (var box in new[] { InHour, InMinute, OutHour, OutMinute }) box.IsEnabled = true;

        UnlockPunchButton.IsEnabled = false;
        UnlockPunchButton.Content = "申請書提出確認済み ✔";
        PunchHintText.Text = "打刻(実績)を直せます。打刻が3件以上ある日は、出勤と退勤の2件に直ります。";

        // 何を根拠に直したかが残るよう、備考へ確認済みの記録を入れる
        if (!NoteInput.Text.Contains(ConfirmedNote))
            NoteInput.Text = NoteInput.Text.Trim().Length == 0
                ? ConfirmedNote
                : $"{NoteInput.Text.Trim()} / {ConfirmedNote}";

        InHour.Focus();
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        // シフト側だけを空にする。打刻はここでは消さない。
        ShiftResult = "";
        PlannedEndResult = "";
        NoteResult = NoteInput.Text.Trim();
        DialogResult = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // ---- シフト(予定) ----
        if (CodeList.SelectedItem is CodeChoice { Value.Length: > 0 } choice)
        {
            ShiftResult = choice.Value;
        }
        else
        {
            // シフト表は「7:30」(時が1桁)の表記
            if (!TryReadTime(StartHour, StartMinute, "シフトの開始", padHours: false, out var start)) return;
            ShiftResult = start;
        }

        // ---- 予定終了時刻 ----
        // シフト表と同じ「16:30」の表記。空欄なら判定側で補完する。
        if (!TryReadTime(EndHour, EndMinute, "シフトの終了", padHours: false, out var end)) return;
        PlannedEndResult = end;

        // ---- 打刻(実績) ----
        // 申請書の提出を確認した場合だけ差し替える(押していなければ原値のまま)
        if (_punchUnlocked)
        {
            if (!TryReadTime(InHour, InMinute, "打刻の出勤", padHours: true, out var punchIn)) return;
            if (!TryReadTime(OutHour, OutMinute, "打刻の退勤", padHours: true, out var punchOut)) return;
            PunchResult = string.Join(" ", new[] { punchIn, punchOut }.Where(t => t.Length > 0));
        }

        NoteResult = NoteInput.Text.Trim();

        DialogResult = true;
    }

    private bool TryReadTime(ComboBox hourBox, ComboBox minuteBox, string label, bool padHours, out string formatted)
    {
        formatted = "";
        var hour = (hourBox.Text ?? "").Trim();
        var minute = (minuteBox.Text ?? "").Trim();
        if (hour.Length == 0 && minute.Length == 0) return true;

        // 時の欄に「0730」「7:30」とまとめて入力された場合も受け付ける
        if (minute.Length == 0 && (hour.Contains(':') || hour.Length >= 3) &&
            TimeText.TryParse(hour, out var whole))
        {
            formatted = TimeText.Format(whole, padHours);
            return true;
        }

        if (hour.Length == 0) return Warn(hourBox, $"{label} の「時」を選んでください。");
        if (minute.Length == 0) minute = "0";

        if (!TimeText.TryParse($"{hour}:{minute}", out var time))
            return Warn(hourBox, $"{label} の時刻を読み取れません: 「{hour}:{minute}」");

        formatted = TimeText.Format(time, padHours);
        return true;
    }

    private bool Warn(ComboBox box, string message)
    {
        MessageBox.Show(this, message, "入力の確認", MessageBoxButton.OK, MessageBoxImage.Warning);
        box.Focus();
        return false;
    }
}
