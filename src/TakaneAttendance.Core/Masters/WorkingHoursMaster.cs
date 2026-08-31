using System.Globalization;
using System.Xml.Linq;

namespace TakaneAttendance.Core.Masters;

/// <summary>
/// 所定労働時間の1区間(月/日 〜 月/日)。年をまたぐ区間(10/16〜2/15)も表せる。
/// </summary>
public sealed class WorkingHoursPeriod
{
    /// <summary>開始の月日(月*100+日)</summary>
    public required int From { get; init; }
    /// <summary>終了の月日(月*100+日)。From より小さい場合は年をまたぐ。</summary>
    public required int To { get; init; }
    /// <summary>平日の所定労働時間(分)</summary>
    public required int WeekdayMinutes { get; init; }
    /// <summary>土日祝の所定労働時間(分)</summary>
    public required int HolidayMinutes { get; init; }
    /// <summary>
    /// この期間の平日の休憩(分)。null なら所属部の既定(<see cref="WorkingHoursDivision.WeekdayBreakMinutes"/>)を使う。
    /// 休憩も時期で変わる所属部があるため、期間ごとに上書きできるようにしている。
    /// </summary>
    public int? WeekdayBreakMinutes { get; init; }
    /// <summary>この期間の土日祝の休憩(分)。null なら所属部の既定を使う。</summary>
    public int? HolidayBreakMinutes { get; init; }

    /// <summary>この期間で上書きされた休憩(分)。指定が無ければ null。</summary>
    public int? BreakMinutesOf(bool weekendOrHoliday)
        => weekendOrHoliday ? HolidayBreakMinutes : WeekdayBreakMinutes;

    /// <summary>休憩を期間で上書きしているか。</summary>
    public bool HasOwnBreak => WeekdayBreakMinutes != null || HolidayBreakMinutes != null;

    public bool Contains(DateOnly date)
    {
        int md = date.Month * 100 + date.Day;
        return From <= To ? md >= From && md <= To     // 4/1〜6/30
                          : md >= From || md <= To;     // 10/16〜2/15(年またぎ)
    }

    public int MinutesOf(bool weekendOrHoliday) => weekendOrHoliday ? HolidayMinutes : WeekdayMinutes;

    public string Describe()
    {
        string R(int md) => $"{md / 100}/{md % 100}";
        var hours = WeekdayMinutes == HolidayMinutes
            ? Hours(WeekdayMinutes)
            : $"平日{Hours(WeekdayMinutes)} 土日祝{Hours(HolidayMinutes)}";

        var rest = !HasOwnBreak ? ""
            : WeekdayBreakMinutes == HolidayBreakMinutes
                ? $" 休憩{WeekdayBreakMinutes}分"
                : $" 休憩 平日{Text(WeekdayBreakMinutes)} 土日祝{Text(HolidayBreakMinutes)}";

        return $"{R(From)}〜{R(To)} {hours}{rest}";

        static string Text(int? minutes) => minutes is { } m ? $"{m}分" : "既定";
    }

    internal static string Hours(int minutes) =>
        minutes % 60 == 0 ? $"{minutes / 60}H" : $"{minutes / 60.0:0.#}H";
}

/// <summary>所属部(業務部・総務部・食堂部・コース管理部 など)ごとの所定労働時間。</summary>
public sealed class WorkingHoursDivision
{
    /// <summary>所属部の名称。従業員マスタの division と同じ文字で突き合わせる。</summary>
    public required string Name { get; init; }
    /// <summary>平日の休憩(分)の既定。予定終了 = 予定開始 + 所定 + 休憩。期間で上書きできる。</summary>
    public int WeekdayBreakMinutes { get; init; } = 90;
    /// <summary>土日祝の休憩(分)の既定。平日と違う場合に使う。期間で上書きできる。</summary>
    public int HolidayBreakMinutes { get; init; } = 90;

    /// <summary>所属部の既定の休憩(分)。期間で上書きしていない日に使う。</summary>
    public int BreakMinutesOf(bool weekendOrHoliday) => weekendOrHoliday ? HolidayBreakMinutes : WeekdayBreakMinutes;
    public List<WorkingHoursPeriod> Periods { get; } = new();

    public WorkingHoursPeriod? PeriodOf(DateOnly date) => Periods.FirstOrDefault(p => p.Contains(date));

    /// <summary>
    /// その日の休憩(分)。期間に休憩の指定があればそれを使い、無ければ所属部の既定を使う。
    /// 休憩は「所属部 × 期間 × 平日/土日祝」の3つで決まる。
    /// </summary>
    public int BreakMinutesOf(DateOnly date, bool weekendOrHoliday)
        => PeriodOf(date)?.BreakMinutesOf(weekendOrHoliday) ?? BreakMinutesOf(weekendOrHoliday);

    /// <summary>期間ごとに休憩を上書きしている行があるか(処理ログの要約に使う)。</summary>
    public bool HasPeriodBreaks => Periods.Any(p => p.HasOwnBreak);
}

/// <summary>
/// 所定労働時間マスタ(統合仕様書 v3.0 第14.5章「変形労働・シーズン設定」/ 要確認 Q-02)。
///
/// 年間カレンダーの【部門別所定労働時間】を、所属部ごとに登録する。
/// 予定終了時刻は「予定開始 + 所定労働時間 + 休憩」で求めるため、
/// シーズンによって早退・時間外の基準が動く。
///
/// 引き当ては従業員マスタの「所属部」で行う。
/// シフト表や打刻データに書かれている部門(営業課・ハウス など)ではないことに注意。
///
/// XML書式(working_hours.xml):
///   &lt;workingHours&gt;
///     &lt;division name="業務部" weekdayBreak="90" holidayBreak="60"&gt;
///       &lt;period from="4/1" to="6/30" weekday="8" holiday="8.5" weekdayBreak="90" holidayBreak="60"/&gt;
///       &lt;period from="4/1" to="5/15" hours="8" breakMinutes="45"/&gt;
///     &lt;/division&gt;
///   &lt;/workingHours&gt;
///
/// 休憩は「所属部 × 期間 × 平日/土日祝」の3つで決まる。
/// period に休憩を書くとその期間だけ上書きし、書かなければ division の値(既定)を使う。
///
/// 登録の無い所属部は、判定閾値マスタの拘束時間(既定9時間30分)で予定終了を決める。
/// </summary>
public sealed class WorkingHoursMaster
{
    /// <summary>読み込み時の注意・エラー。</summary>
    public List<string> Messages { get; } = new();

    private readonly List<WorkingHoursDivision> _divisions = new();

    public IReadOnlyList<WorkingHoursDivision> Divisions => _divisions;
    public bool HasEntries => _divisions.Count > 0;

    /// <summary>処理ログに出す1行の要約。</summary>
    public string Summary => _divisions.Count == 0
        ? "所定労働時間マスタなし(予定終了は判定閾値マスタの拘束時間で決めます)"
        : string.Join(" , ", _divisions.Select(d => $"{d.Name} {d.Periods.Count}区間 " +
              (d.WeekdayBreakMinutes == d.HolidayBreakMinutes
                  ? $"(休憩{d.WeekdayBreakMinutes}分"
                  : $"(休憩 平日{d.WeekdayBreakMinutes}分 / 土日祝{d.HolidayBreakMinutes}分") +
              (d.HasPeriodBreaks ? $" ※{d.Periods.Count(p => p.HasOwnBreak)}区間は期間ごとの休憩)" : ")")));

    public void Add(WorkingHoursDivision division) => _divisions.Add(division);

    /// <summary>所属部の登録。名前が一致しなければ null。</summary>
    public WorkingHoursDivision? DivisionOf(string division)
        => division.Length == 0 ? null : _divisions.FirstOrDefault(d => d.Name == division);

    /// <summary>その所属部・その日の所定労働時間(分)。登録が無ければ null。</summary>
    public int? MinutesOf(string division, DateOnly date, bool weekendOrHoliday)
        => DivisionOf(division)?.PeriodOf(date)?.MinutesOf(weekendOrHoliday);

    /// <summary>
    /// その所属部・その日の拘束時間(分) = 所定労働時間 + 休憩。
    /// 予定終了 = 予定開始 + この値。登録が無ければ null。
    /// </summary>
    public int? SpanMinutesOf(string division, DateOnly date, bool weekendOrHoliday)
    {
        var entry = DivisionOf(division);
        var period = entry?.PeriodOf(date);
        return period == null
            ? null
            // 休憩は期間の指定が優先。無ければ所属部の既定
            : period.MinutesOf(weekendOrHoliday)
              + (period.BreakMinutesOf(weekendOrHoliday) ?? entry!.BreakMinutesOf(weekendOrHoliday));
    }

    /// <summary>
    /// その所属部・その日の休憩(分)。所属部の登録が無ければ null。
    /// 「所属部 × 期間 × 平日/土日祝」で決まる。
    /// </summary>
    public int? BreakMinutesOf(string division, DateOnly date, bool weekendOrHoliday)
        => DivisionOf(division)?.BreakMinutesOf(date, weekendOrHoliday);

    public static WorkingHoursMaster Load(string? xmlPath)
    {
        var m = new WorkingHoursMaster();
        var root = MasterXml.LoadRoot(xmlPath, "所定労働時間マスタ", m.Messages);
        if (root == null) return m;

        foreach (var e in root.Elements("division"))
        {
            var name = e.Attr("name");
            if (name.Length == 0)
            {
                m.Messages.Add("[W-MS-014] 所定労働時間マスタに所属部の名前が無い行があります。この行は無視します。");
                continue;
            }

            // breakMinutes は平日・土日祝の共通値。個別に決める場合は weekdayBreak / holidayBreak
            int commonBreak = ParseBreak(e.Attr("breakMinutes"), name, "breakMinutes", m) ?? 90;
            int weekdayBreak = ParseBreak(e.Attr("weekdayBreak"), name, "weekdayBreak", m) ?? commonBreak;
            int holidayBreak = ParseBreak(e.Attr("holidayBreak"), name, "holidayBreak", m) ?? commonBreak;

            var division = new WorkingHoursDivision
            {
                Name = name,
                WeekdayBreakMinutes = weekdayBreak,
                HolidayBreakMinutes = holidayBreak
            };

            foreach (var p in e.Elements("period"))
            {
                if (!TryParseMonthDay(p.Attr("from"), out var from) || !TryParseMonthDay(p.Attr("to"), out var to))
                {
                    m.Messages.Add($"[W-MS-014] 所定労働時間マスタ「{name}」の期間 '{p.Attr("from")}〜{p.Attr("to")}' を読めません。" +
                                   "「月/日」の形で書いてください。この行は無視します。");
                    continue;
                }

                // hours を書いた場合は平日・土日祝とも同じ時間になる
                int? common = ParseHours(p.Attr("hours"));
                int? weekday = ParseHours(p.Attr("weekday")) ?? common;
                int? holiday = ParseHours(p.Attr("holiday")) ?? common ?? weekday;

                if (weekday == null)
                {
                    m.Messages.Add($"[W-MS-014] 所定労働時間マスタ「{name}」{p.Attr("from")}〜{p.Attr("to")} に所定労働時間がありません。" +
                                   "hours か weekday を指定してください。この行は無視します。");
                    continue;
                }

                // 休憩も時期で変わる所属部があるため、期間ごとに上書きできる。
                // 書かれていない期間は所属部の既定(上の weekdayBreak / holidayBreak)を使う。
                var label = $"{name} {p.Attr("from")}〜{p.Attr("to")}";
                int? periodCommonBreak = ParseBreak(p.Attr("breakMinutes"), label, "breakMinutes", m);
                int? periodWeekdayBreak = ParseBreak(p.Attr("weekdayBreak"), label, "weekdayBreak", m) ?? periodCommonBreak;
                int? periodHolidayBreak = ParseBreak(p.Attr("holidayBreak"), label, "holidayBreak", m) ?? periodCommonBreak;

                division.Periods.Add(new WorkingHoursPeriod
                {
                    From = from,
                    To = to,
                    WeekdayMinutes = weekday.Value,
                    HolidayMinutes = holiday ?? weekday.Value,
                    WeekdayBreakMinutes = periodWeekdayBreak,
                    HolidayBreakMinutes = periodHolidayBreak
                });
            }

            if (m.DivisionOf(name) != null)
            {
                m.Messages.Add($"[W-MS-014] 所定労働時間マスタに所属部「{name}」が2つ以上あります。先に書かれた方を使います。");
                continue;
            }

            m.Add(division);
        }

        // 旧書式(グループ + <department>)のファイルが残っている場合に気づけるようにする
        if (_HasLegacyGroups(root))
            m.Messages.Add("[W-MS-014] 所定労働時間マスタに古い書式の <group> があります。" +
                           "所属部ごとの <division name=\"業務部\"> … に書き換えてください(この行は読み飛ばしました)。");

        return m;
    }

    private static bool _HasLegacyGroups(XElement root) => root.Elements("group").Any();

    /// <summary>休憩(分)を読む。書かれていなければ null(所属部の既定を使う)。</summary>
    private static int? ParseBreak(string text, string where, string attribute, WorkingHoursMaster m)
    {
        if (text.Length == 0) return null;
        if (int.TryParse(text, out var minutes) && minutes is >= 0 and <= 600) return minutes;

        m.Messages.Add($"[W-MS-014] 所定労働時間マスタ「{where}」の {attribute} '{text}' を読めません。" +
                       "0〜600 の分で書いてください。この指定は無視します。");
        return null;
    }

    /// <summary>「4/1」「04/01」を 月*100+日 に直す(画面の入力チェックからも使う)。</summary>
    public static bool TryParseMonthDay(string text, out int monthDay)
    {
        monthDay = 0;
        var parts = text.Split('/', '-');
        if (parts.Length != 2) return false;
        if (!int.TryParse(parts[0], out var month) || !int.TryParse(parts[1], out var day)) return false;
        if (month is < 1 or > 12 || day is < 1 or > 31) return false;
        monthDay = month * 100 + day;
        return true;
    }

    /// <summary>「8」「8.5」「7.5」を分に直す(画面の入力チェックからも使う)。</summary>
    public static int? ParseHours(string text)
    {
        if (text.Length == 0) return null;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var hours)) return null;
        if (hours <= 0 || hours > 24) return null;
        return (int)Math.Round(hours * 60);
    }
}
