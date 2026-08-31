namespace TakaneAttendance.Core.Models;

/// <summary>
/// 勤務区分の種別。統合仕様書 v3.0 第8.3章・第17章の6区分。
/// 勤務区分マスタ(shift_type.xml)でシフト表の文字値と対応づける。
/// </summary>
public enum ShiftKind
{
    /// <summary>通常勤務(予定開始時刻あり)</summary>
    Work,
    /// <summary>公休</summary>
    DayOff,
    /// <summary>有給</summary>
    PaidLeave,
    /// <summary>出張(終日・半日は打刻件数で判定する)</summary>
    BusinessTrip,
    /// <summary>その他(欠勤・特別休暇など。内訳は要確認 Q-04)</summary>
    Other,
    /// <summary>対象外(休場日・在籍外など。画面はグレー表示)</summary>
    Excluded,
    /// <summary>マスタ未登録の文字値。「その他」として扱う</summary>
    Unknown
}

/// <summary>雇用区分。早退の判定基準が分かれる(仕様書 v3.0 第13.2章・第14.4章)。</summary>
public enum EmploymentType
{
    /// <summary>正社員。早退基準は「予定開始 + 拘束9時間30分」</summary>
    FullTime,
    /// <summary>パート。早退基準は予定終了時刻</summary>
    PartTime,
    /// <summary>アルバイト。早退基準は予定終了時刻</summary>
    Arbeit
}

/// <summary>シフトと打刻の突合状態(仕様書 v3.0 第11.3章)。</summary>
public enum MatchStatus
{
    /// <summary>シフト・打刻の双方あり</summary>
    Both,
    /// <summary>シフトあり・打刻なし</summary>
    ShiftOnly,
    /// <summary>シフトなし・打刻あり</summary>
    PunchOnly,
    /// <summary>氏名不一致・重複。自動突合しない</summary>
    Unresolved
}

/// <summary>
/// 主判定(仕様書 v3.0 第13.1章)。
///
/// 1日に複数の判定コードが付くため、表示はこの区分の優先順位で1つに絞る。
/// 優先順位は「要確認 ＞ 遅刻 ＞ 早退 ＞ 早出 ＞ 時間外 ＞ 正常」。
/// 数値が大きいほど優先度が高い。
/// </summary>
public enum Judgement
{
    /// <summary>正常。4行すべて白背景</summary>
    Normal = 0,
    /// <summary>時間外30分以上</summary>
    Overtime = 1,
    /// <summary>早出30分以上</summary>
    EarlyIn = 2,
    /// <summary>早退</summary>
    EarlyLeave = 3,
    /// <summary>遅刻</summary>
    Late = 4,
    /// <summary>要確認(打刻漏れ・複数打刻・公休打刻など)</summary>
    Review = 5,
    /// <summary>対象外(休場日など)。他の判定と併存しない</summary>
    Excluded = 6
}

/// <summary>主判定の画面表示(仕様書 v3.0 第15.1章の背景色)。</summary>
public static class JudgementInfo
{
    private static readonly Dictionary<Judgement, (string Name, string ColorHex)> Map = new()
    {
        [Judgement.Normal]     = ("正常",   "FFFFFF"),
        // 時間外は提出する帳票に載せず、画面の件数だけで確認する。正常と同じ白のままにする。
        [Judgement.Overtime]   = ("時間外", "FFFFFF"),
        [Judgement.EarlyIn]    = ("早出",   "FCE5CD"),
        [Judgement.EarlyLeave] = ("早退",   "D9EAD3"),
        [Judgement.Late]       = ("遅刻",   "F4CCCC"),
        [Judgement.Review]     = ("要確認", "E4D7F5"),
        [Judgement.Excluded]   = ("対象外", "E7E6E6"),
    };

    /// <summary>区分名(集計・凡例用)</summary>
    public static string Name(Judgement j) => Map[j].Name;

    /// <summary>日付セル4行に塗る背景色(RRGGBB)</summary>
    public static string ColorHex(Judgement j) => Map[j].ColorHex;
}

/// <summary>判定コード。統合仕様書 v3.0 付録A の一覧に準拠。</summary>
public enum ResultCode
{
    /// <summary>DATA_ERROR 入力値解析不能</summary>
    DataError,
    /// <summary>NAME_UNRESOLVED 社員名未解決</summary>
    NameUnresolved,
    /// <summary>NO_PUNCH 打刻0件または1件(出退勤を区別しない)</summary>
    NoPunch,
    /// <summary>MULTI_PUNCH 重複除外後の打刻が3件以上</summary>
    MultiPunch,
    /// <summary>NO_SHIFT_PUNCH シフトなし・打刻あり</summary>
    NoShiftPunch,
    /// <summary>DAY_OFF 公休・打刻なし</summary>
    DayOff,
    /// <summary>DAY_OFF_PUNCH 公休に打刻</summary>
    DayOffPunch,
    /// <summary>PAID_LEAVE 有給・打刻なし</summary>
    PaidLeave,
    /// <summary>PAID_LEAVE_PUNCH 有給日に打刻</summary>
    PaidLeavePunch,
    /// <summary>BUSINESS_TRIP_FULL 終日出張(打刻なし)。申請書確認メッセージを表示する</summary>
    BusinessTripFull,
    /// <summary>BUSINESS_TRIP_HALF 半日出張(打刻2件)。遅刻・早退を判定しない</summary>
    BusinessTripHalf,
    /// <summary>OTHER その他・打刻なし</summary>
    Other,
    /// <summary>OTHER_PUNCH その他に打刻</summary>
    OtherPunch,
    /// <summary>EXCLUDED 対象外(休場日など)</summary>
    Excluded,
    /// <summary>LATE 予定開始超過(1分から対象)</summary>
    Late,
    /// <summary>EARLY_LEAVE 必要退勤時刻未満</summary>
    EarlyLeave,
    /// <summary>EARLY_IN_30 30分以上の早出</summary>
    EarlyIn30,
    /// <summary>OVERTIME_30 30分以上の時間外</summary>
    Overtime30,
    /// <summary>NORMAL 上記非該当</summary>
    Normal
}

/// <summary>
/// 判定コードの画面表示・主判定区分。
///
/// Label   … 画面の判定結果行に出す文言(仕様書 v3.0 第13.2章「画面表示」列)。
/// Judgement … 背景色と主判定の優先順位を決める区分。
/// Code    … 付録A のコード名。ログ・帳票の突き合わせに使う。
/// </summary>
public static class ResultCodeInfo
{
    private static readonly Dictionary<ResultCode, (string Code, string Label, Judgement Judgement, string Description)> Map = new()
    {
        [ResultCode.DataError]         = ("DATA_ERROR",          "要確認",  Judgement.Review,     "入力値を解析できません"),
        [ResultCode.NameUnresolved]    = ("NAME_UNRESOLVED",     "要確認",  Judgement.Review,     "社員名を正式氏名へ解決できません"),
        [ResultCode.NoPunch]           = ("NO_PUNCH",            "打刻漏れ", Judgement.Review,    "打刻が0件または1件です"),
        [ResultCode.MultiPunch]        = ("MULTI_PUNCH",         "要確認",  Judgement.Review,     "打刻が3件以上あります"),
        [ResultCode.NoShiftPunch]      = ("NO_SHIFT_PUNCH",      "要確認",  Judgement.Review,     "予定シフトがなく打刻があります"),
        [ResultCode.DayOff]            = ("DAY_OFF",             "-",      Judgement.Normal,     "公休"),
        [ResultCode.DayOffPunch]       = ("DAY_OFF_PUNCH",       "要確認",  Judgement.Review,     "公休日に打刻があります"),
        [ResultCode.PaidLeave]         = ("PAID_LEAVE",          "-",      Judgement.Normal,     "有給"),
        [ResultCode.PaidLeavePunch]    = ("PAID_LEAVE_PUNCH",    "要確認",  Judgement.Review,     "有給日に打刻があります"),
        [ResultCode.BusinessTripFull]  = ("BUSINESS_TRIP_FULL",  "-",      Judgement.Normal,     "終日出張。出張申請書を提出済みか確認してください"),
        [ResultCode.BusinessTripHalf]  = ("BUSINESS_TRIP_HALF",  "-",      Judgement.Normal,     "半日出張。遅刻・早退は判定しません"),
        [ResultCode.Other]             = ("OTHER",               "-",      Judgement.Normal,     "その他"),
        [ResultCode.OtherPunch]        = ("OTHER_PUNCH",         "要確認",  Judgement.Review,     "勤務区分「その他」の日に打刻があります"),
        [ResultCode.Excluded]          = ("EXCLUDED",            "対象外",  Judgement.Excluded,   "対象外"),
        [ResultCode.Late]              = ("LATE",                "遅",     Judgement.Late,       "予定開始時刻を過ぎた出勤です"),
        [ResultCode.EarlyLeave]        = ("EARLY_LEAVE",         "早",     Judgement.EarlyLeave, "必要退勤時刻より早い退勤です"),
        [ResultCode.EarlyIn30]         = ("EARLY_IN_30",         "早出",    Judgement.EarlyIn,    "予定開始より30分以上早い出勤です"),
        [ResultCode.Overtime30]        = ("OVERTIME_30",         "時間外",  Judgement.Overtime,   "予定終了より30分以上遅い退勤です"),
        [ResultCode.Normal]            = ("NORMAL",              "-",      Judgement.Normal,     "正常"),
    };

    /// <summary>付録A のコード名(NO_PUNCH など)。ログ・帳票に使う。</summary>
    public static string CodeName(ResultCode code) => Map[code].Code;

    /// <summary>画面の判定結果行に出す文言(要確認 / 打刻漏れ / 遅 / 早 / 早出 / 時間外 / -)。</summary>
    public static string Label(ResultCode code) => Map[code].Label;

    /// <summary>背景色と主判定の優先順位を決める区分。</summary>
    public static Judgement JudgementOf(ResultCode code) => Map[code].Judgement;

    /// <summary>ツールチップ・詳細表示に使う説明。</summary>
    public static string Description(ResultCode code) => Map[code].Description;
}

/// <summary>社員名の解決結果。</summary>
public sealed class PersonRef
{
    /// <summary>元ファイルに書かれていた氏名(原文)</summary>
    public required string SourceName { get; init; }
    /// <summary>空白除去などを行った正規化後の氏名</summary>
    public required string NormalizedName { get; init; }
    /// <summary>別名マスタ等で解決した正式氏名。解決できない場合は null。</summary>
    public string? CanonicalName { get; init; }
    /// <summary>部門(取得できた場合)</summary>
    public string? Department { get; init; }
    /// <summary>タイムレコーダーの作業番号(打刻データのみ)。表示用で突合キーには使わない。</summary>
    public string? EmployeeNo { get; init; }
    /// <summary>
    /// 雇用区分(従業員マスタから解決)。早退の判定基準が分かれる。
    /// マスタに登録が無い場合は正社員として扱う(仕様書 v3.0 Q-03 の確定待ち)。
    /// </summary>
    public EmploymentType Employment { get; init; } = EmploymentType.FullTime;

    /// <summary>
    /// 突合に使用するキー。正式氏名を正規化した文字列(未解決の場合は正規化名)。
    /// 別名マスタに書かれた正式氏名と、打刻データ上の氏名とで空白の入り方が違っても
    /// 同一人物として突合できるよう、必ず正規化した値を使う。
    /// </summary>
    public required string Key { get; init; }
    public bool IsResolved => CanonicalName != null;
    /// <summary>画面表示用の氏名</summary>
    public string DisplayName => CanonicalName ?? SourceName;
}

/// <summary>
/// シフト表に載っている社員1件(記載順)。
/// 出席記録レポートの社員一覧・並び順・部門は、この一覧を基準にする。
/// </summary>
public sealed class ShiftRosterEntry
{
    /// <summary>突合キー(正規化した正式氏名)</summary>
    public required string Key { get; init; }
    /// <summary>シフト表の表記(例「平山 部長」)</summary>
    public required string SourceName { get; init; }
    /// <summary>正式氏名。解決できていない場合はシフト表の表記のまま。</summary>
    public required string DisplayName { get; init; }
    /// <summary>シフト表の部門(競技課・営業課 など)</summary>
    public required string Department { get; init; }
    /// <summary>シフト表での並び順(0始まり)</summary>
    public required int Order { get; init; }
    /// <summary>読み取り元の行(エラー追跡用)</summary>
    public required int SourceRow { get; init; }
}

/// <summary>
/// 打刻データに載っている社員1件(記載順)。
///
/// 出席記録レポートの並び順・氏名・部門は、この一覧を基準にする。
/// お客様が手作業で作っている出席記録は、タイムレコーダーの出力をそのまま複製したものであり、
/// 並び順も氏名も部門も打刻データ側の表記になっているため。
/// </summary>
public sealed class PunchRosterEntry
{
    /// <summary>突合キー(正規化した正式氏名)</summary>
    public required string Key { get; init; }
    /// <summary>打刻データの氏名(例「原田　祥吾」)。これが正式氏名になる。</summary>
    public required string DisplayName { get; init; }
    /// <summary>打刻データの部門(ハウス・セルフポーター など)</summary>
    public required string Department { get; init; }
    /// <summary>作業番号</summary>
    public required string EmployeeNo { get; init; }
    /// <summary>打刻データでの並び順(0始まり)</summary>
    public required int Order { get; init; }
    /// <summary>読み取り元の行(エラー追跡用)</summary>
    public required int SourceRow { get; init; }
}

/// <summary>シフト表から読み取った1人1日分の勤務予定。</summary>
public sealed class ShiftDaily
{
    public required PersonRef Person { get; init; }
    public required DateOnly WorkDate { get; init; }
    /// <summary>セルの原文(時刻セルは "7:30" のように整形済み)</summary>
    public required string RawValue { get; init; }
    public required ShiftKind Kind { get; init; }
    /// <summary>勤務区分の文字値(公・有・欠・出張など)。時刻セルの場合は null。</summary>
    public string? ShiftTypeCode { get; init; }
    /// <summary>予定開始時刻</summary>
    public TimeSpan? PlannedStart { get; init; }
    /// <summary>予定終了時刻(勤務パターン・雇用区分から補完。画面で修正可能。未確定なら null)</summary>
    public TimeSpan? PlannedEnd { get; set; }
    /// <summary>備考(画面での修正理由・申請書確認など。仕様書 v3.0 第8.3章)</summary>
    public string Note { get; set; } = "";
    /// <summary>読み取り元の位置(エラー追跡用)</summary>
    public required string SourceCell { get; init; }
}

/// <summary>
/// MB20 から読み取った1人1日分の打刻実績(仕様書 v3.0 第12章)。
///
/// 打刻は読取専用で、画面では修正しない。原文と抽出した全時刻を保持し、
/// 帳票・打刻詳細一覧で全件を確認できるようにする。
/// </summary>
public sealed class PunchDaily
{
    public required PersonRef Person { get; init; }
    public required DateOnly WorkDate { get; init; }
    /// <summary>セル原文。出席記録レポートへはこの値をそのまま(丸めずに)出力する。</summary>
    public required string RawValue { get; init; }
    /// <summary>
    /// セルから抽出した全ての時刻(記載順)。
    /// 複数ファイルを統合した場合、同一社員・同一日付・同一時刻の完全一致は1件にまとめる。
    /// </summary>
    public required IReadOnlyList<TimeSpan> Times { get; init; }

    /// <summary>重複除外後の打刻件数。0/1件は打刻漏れ、3件以上は要確認。</summary>
    public int PunchCount => Times.Count;

    /// <summary>画面の「打刻1回目」。1件のみの場合もここに表示する。</summary>
    public TimeSpan? FirstPunch => Times.Count >= 1 ? Times[0] : null;
    /// <summary>画面の「最終打刻」。2件以上ある場合のみ確定する。</summary>
    public TimeSpan? LastPunch => Times.Count >= 2 ? Times[^1] : null;

    /// <summary>判定・計算に使う出勤時刻(打刻2件以上のときのみ)。</summary>
    public TimeSpan? ActualIn => Times.Count >= 2 ? Times[0] : null;
    /// <summary>判定・計算に使う退勤時刻(打刻2件以上のときのみ)。</summary>
    public TimeSpan? ActualOut => Times.Count >= 2 ? Times[^1] : null;

    public required string SourceCell { get; init; }
}

/// <summary>
/// 1日分の勤務時間(Blocker B-04 / B-05)。
///
/// 打刻を15分単位に丸めたうえで、拘束時間の長さから休憩時間を自動で決め、
/// 実労働時間を求めたもの。丸めの単位・方向・休憩の帯は break_rule.xml で変更できる。
/// </summary>
public sealed class WorkTime
{
    /// <summary>丸めた出勤時刻</summary>
    public required TimeSpan RoundedIn { get; init; }
    /// <summary>丸めた退勤時刻</summary>
    public required TimeSpan RoundedOut { get; init; }
    /// <summary>拘束時間(丸めた退勤 - 丸めた出勤)</summary>
    public required int SpanMinutes { get; init; }
    /// <summary>自動計算した休憩時間</summary>
    public required int BreakMinutes { get; init; }
    /// <summary>適用した帯の説明(例「6時間以内 → 休憩15分」)。根拠を追えるようにする。</summary>
    public string AppliedBand { get; init; } = "";

    /// <summary>実労働時間(拘束 - 休憩)。休憩の方が長い場合は 0。</summary>
    public int WorkMinutes => Math.Max(0, SpanMinutes - BreakMinutes);

    public static string Hm(int minutes) => $"{minutes / 60}:{minutes % 60:00}";
    public static string Hm(TimeSpan t) => $"{(int)t.TotalHours:00}:{t.Minutes:00}";

    public string RoundedRangeText => $"{Hm(RoundedIn)}〜{Hm(RoundedOut)}";
    public string SpanText => Hm(SpanMinutes);
    public string BreakText => Hm(BreakMinutes);
    public string WorkText => Hm(WorkMinutes);
}

/// <summary>突合・判定の結果(1人1日)。</summary>
public sealed class AttendanceDaily
{
    public required PersonRef Person { get; init; }
    public required DateOnly WorkDate { get; init; }
    public ShiftDaily? Shift { get; init; }
    public PunchDaily? Punch { get; init; }
    public required MatchStatus MatchStatus { get; init; }
    public List<ResultCode> ResultCodes { get; } = new();

    /// <summary>
    /// 15分丸めと休憩の自動計算の結果。通常勤務で出退勤が揃った日だけ入る。
    /// (公休・有給や打刻漏れの日は null)
    /// </summary>
    public WorkTime? WorkTime { get; set; }

    /// <summary>
    /// 主判定の区分(仕様書 v3.0 第13.1章)。
    /// 「要確認 ＞ 遅刻 ＞ 早退 ＞ 早出 ＞ 時間外 ＞ 正常」の優先順位で1つに絞る。
    /// </summary>
    public Judgement Judgement =>
        ResultCodes.Count == 0
            ? Judgement.Normal
            : ResultCodes.Max(ResultCodeInfo.JudgementOf);

    /// <summary>主判定として表示する判定コード。同じ区分の中では先に付いたものを使う。</summary>
    public ResultCode PrimaryCode
    {
        get
        {
            if (ResultCodes.Count == 0) return ResultCode.Normal;
            var top = Judgement;
            return ResultCodes.First(c => ResultCodeInfo.JudgementOf(c) == top);
        }
    }

    /// <summary>副判定(主判定以外)。画面の詳細表示で確認できるようにする。</summary>
    public IEnumerable<ResultCode> SecondaryCodes
    {
        get
        {
            var primary = PrimaryCode;
            return ResultCodes.Where(c => c != primary);
        }
    }

    /// <summary>判定結果行に出す文言(要確認 / 打刻漏れ / 遅 / 早退 / 早出 / 時間外 / -)。</summary>
    public string JudgementLabel => ResultCodeInfo.Label(PrimaryCode);

    /// <summary>日付セル4行に塗る背景色(RRGGBB)。正常は白。</summary>
    public string JudgementColorHex => JudgementInfo.ColorHex(Judgement);

    // ---- 画面表示用のプロパティ ----
    public string DateText => WorkDate.ToString("yyyy/MM/dd");
    public string DayOfWeekText => WorkDate.DayOfWeek switch
    {
        DayOfWeek.Sunday => "日", DayOfWeek.Monday => "月", DayOfWeek.Tuesday => "火",
        DayOfWeek.Wednesday => "水", DayOfWeek.Thursday => "木", DayOfWeek.Friday => "金", _ => "土"
    };
    public string PersonName => Person.DisplayName;
    public string Department => Person.Department ?? "";
    public string EmployeeNo => Person.EmployeeNo ?? "";

    /// <summary>4行表示の1行目。予定シフト(勤務区分の文字値または予定開始時刻)。</summary>
    public string ShiftText => Shift?.RawValue ?? "";
    public string PlannedStartText => Shift?.PlannedStart is { } t ? $"{(int)t.TotalHours:00}:{t.Minutes:00}" : "";
    public string PlannedEndText => Shift?.PlannedEnd is { } t ? $"{(int)t.TotalHours:00}:{t.Minutes:00}" : "";

    /// <summary>4行表示の2行目。打刻1回目。打刻が無い日は「-」。</summary>
    public string FirstPunchText => Punch?.FirstPunch is { } t ? $"{(int)t.TotalHours:00}:{t.Minutes:00}" : "-";
    /// <summary>4行表示の3行目。最終打刻。1件以下の日は「-」。</summary>
    public string LastPunchText => Punch?.LastPunch is { } t ? $"{(int)t.TotalHours:00}:{t.Minutes:00}" : "-";

    public string ActualInText => Punch?.ActualIn is { } t ? $"{(int)t.TotalHours:00}:{t.Minutes:00}" : "";
    public string ActualOutText => Punch?.ActualOut is { } t ? $"{(int)t.TotalHours:00}:{t.Minutes:00}" : "";
    public string PunchRawText => Punch?.RawValue ?? "";
    /// <summary>打刻詳細一覧・ツールチップ用。抽出した全打刻。</summary>
    public string AllPunchesText =>
        Punch == null ? "" : string.Join(" ", Punch.Times.Select(t => $"{(int)t.TotalHours:00}:{t.Minutes:00}"));

    // ---- 勤務時間(15分丸め + 休憩の自動計算) ----
    public string RoundedRangeText => WorkTime?.RoundedRangeText ?? "";
    public string SpanText  => WorkTime?.SpanText  ?? "";
    public string BreakText => WorkTime?.BreakText ?? "";
    public string WorkText  => WorkTime?.WorkText  ?? "";
    /// <summary>判定の内訳(詳細表示・ツールチップ用)。</summary>
    public string ResultText => string.Join(" / ", ResultCodes.Select(ResultCodeInfo.Description));
    /// <summary>主判定の区分名(集計・凡例用)。</summary>
    public string JudgementName => JudgementInfo.Name(Judgement);

    /// <summary>遅刻・早退などの差分(分)。表示用。</summary>
    public string DiffText
    {
        get
        {
            var parts = new List<string>();
            if (Shift?.PlannedStart is { } ps && Punch?.ActualIn is { } ai)
            {
                var d = (int)(ai - ps).TotalMinutes;
                if (d != 0) parts.Add(d > 0 ? $"出勤+{d}分" : $"出勤{d}分");
            }
            if (Shift?.PlannedEnd is { } pe && Punch?.ActualOut is { } ao)
            {
                var d = (int)(ao - pe).TotalMinutes;
                if (d != 0) parts.Add(d > 0 ? $"退勤+{d}分" : $"退勤{d}分");
            }
            return string.Join(" ", parts);
        }
    }
}

/// <summary>1回の突合処理の実行結果。</summary>
public sealed class MatchingResult
{
    public required string ExecutionId { get; init; }
    public required DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; set; }
    public int TargetYear { get; set; }
    public int TargetMonth { get; set; }

    public List<AttendanceDaily> Details { get; } = new();
    /// <summary>シフト表に載っている社員(記載順)。帳票に出す社員の範囲は、この一覧を基準にする。</summary>
    public List<ShiftRosterEntry> ShiftRoster { get; } = new();
    /// <summary>打刻データに載っている社員(記載順)。帳票の並び順・氏名・部門は、この一覧を基準にする。</summary>
    public List<PunchRosterEntry> PunchRoster { get; } = new();
    /// <summary>解決できなかった氏名(原文 → 出現元)。別名マスタ整備の材料になる。</summary>
    public List<UnresolvedName> UnresolvedNames { get; } = new();
    /// <summary>処理中に発生した警告・エラーメッセージ(表示用の文字列)</summary>
    public List<string> Messages { get; } = new();

    /// <summary>
    /// コード付きの処理メッセージ(仕様書 v3.0 第19章)。
    /// 実行ログのエラー明細と、処理停止の判定に使う。
    /// </summary>
    public List<ProcessMessage> ProcessMessages { get; } = new();

    /// <summary>処理停止のエラーがあるか。ある場合、突合は行われていない。</summary>
    public bool HasFatalError => ProcessMessages.Any(m => m.Level == MessageLevel.Fatal);

    /// <summary>コード付きのメッセージを積む。処理ログにも同じ内容を出す。</summary>
    public void Add(ProcessMessage message)
    {
        ProcessMessages.Add(message);
        Messages.Add(message.ToString());
    }

    /// <summary>
    /// この実行で読み込んだマスタ一式。
    /// 帳票の生成でもう一度読み直さずに済むよう、実行結果に添えて持たせる。
    /// </summary>
    public Masters.MasterSet? Masters { get; set; }

    public int ShiftRecordCount { get; set; }
    public int PunchRecordCount { get; set; }
    public int PersonCount => Details.Select(d => d.Person.Key).Distinct().Count();

    // 実行ログの「結果」欄(仕様書 v3.0 第18.2章)
    public int NormalCount     => Details.Count(d => d.Judgement == Judgement.Normal);
    public int LateCount       => Details.Count(d => d.Judgement == Judgement.Late);
    public int EarlyLeaveCount => Details.Count(d => d.Judgement == Judgement.EarlyLeave);
    public int EarlyInCount    => Details.Count(d => d.Judgement == Judgement.EarlyIn);
    public int OvertimeCount   => Details.Count(d => d.Judgement == Judgement.Overtime);
    public int ReviewCount     => Details.Count(d => d.Judgement == Judgement.Review);
    public int ExcludedCount   => Details.Count(d => d.Judgement == Judgement.Excluded);
    public TimeSpan Elapsed => FinishedAt - StartedAt;
}

/// <summary>正式氏名に解決できなかった氏名。</summary>
public sealed class UnresolvedName
{
    public required string SourceName { get; init; }
    public required string NormalizedName { get; init; }
    /// <summary>"シフト表" または "打刻データ"</summary>
    public required string Origin { get; init; }
    public string? Department { get; init; }
    public string? EmployeeNo { get; init; }
    public int Occurrences { get; set; }
    /// <summary>別名マスタ(name_alias.xml)へ追記するための候補行</summary>
    public string SuggestedXmlLine =>
        $"<alias source=\"{Escape(SourceName)}\" canonical=\"\" note=\"{Escape(Origin)}\"/>";

    /// <summary>属性値として書ける形にする(氏名に記号が含まれても壊れないように)。</summary>
    private static string Escape(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
