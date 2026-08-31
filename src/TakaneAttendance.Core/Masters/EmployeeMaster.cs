using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Naming;

namespace TakaneAttendance.Core.Masters;

/// <summary>従業員マスタの1件(統合仕様書 v3.0 第10.4章)。</summary>
public sealed class EmployeeEntry
{
    /// <summary>正式氏名。正規化後の基準名。</summary>
    public required string CanonicalName { get; init; }
    /// <summary>社員番号。一覧・帳票の表示用で、突合キーには使わない。</summary>
    public string EmployeeNo { get; init; } = "";
    /// <summary>
    /// 所属部(業務部・総務部・食堂部・コース管理部 など)。
    /// 所定労働時間マスタの引き当てキー。従業員データの「所属」の部の部分。
    /// </summary>
    public string Division { get; init; } = "";
    /// <summary>所属課(業務Ⅰ課・競技課・総務課 など)。表示・帳票振分に使う。</summary>
    public string Department { get; init; } = "";
    /// <summary>雇用区分。早退の判定基準とパート給与計算表の対象を決める。</summary>
    public EmploymentType Employment { get; init; } = EmploymentType.FullTime;
    /// <summary>シフトパターン名(予定終了・休憩の紐付け用)。</summary>
    public string ShiftPattern { get; init; } = "";
    /// <summary>
    /// 1日の拘束時間(休憩込み)。パート・アルバイトの予定終了 = 予定開始 + この時間。
    /// 未設定の方は早退・時間外を判定しない。正社員は所定労働時間マスタを使うため通常は空。
    /// </summary>
    public TimeSpan? WorkHours { get; init; }
    /// <summary>基本時給(円)。パート・アルバイト給与計算表の時給欄。未設定なら空欄のまま出力する。</summary>
    public int? HourlyWage { get; init; }
    /// <summary>在籍開始日。対象年月の在籍判定に使う。未設定なら制限なし。</summary>
    public DateOnly? JoinedOn { get; init; }
    /// <summary>在籍終了日。未設定なら制限なし。</summary>
    public DateOnly? LeftOn { get; init; }
    /// <summary>
    /// 管理区分。オンの方だけを勤怠管理の対象にする(突合結果の一覧にも帳票にも出す)。
    /// オフにすると、その方はシフト表・打刻データに載っていても対象から外れる。
    /// マスタに書かれていない場合はオン(従来どおり全員が対象)。
    /// </summary>
    public bool IsManaged { get; init; } = true;

    /// <summary>対象日に在籍しているか。</summary>
    public bool IsActiveOn(DateOnly date)
        => (JoinedOn is not { } from || date >= from)
        && (LeftOn is not { } to || date <= to);
}

/// <summary>
/// 従業員マスタ(統合仕様書 v3.0 第10.4章・第17章)。
///
/// 雇用区分が早退の判定基準を分けるため(正社員は「予定開始 + 9時間30分」、
/// パート・アルバイトは予定終了時刻)、このマスタが判定の前提になる。
/// パート・アルバイト給与計算表の出力対象もここで決まる。
///
/// 仕様書では入力は Excel(任意)だが、PoC では他のマスタと同じ XML でも保持できるようにし、
/// Excel を受領した時点で <see cref="Register"/> 経由で流し込めるようにしてある。
///
/// XML書式(employee.xml):
///   &lt;employees&gt;
///     &lt;employee no="1001" name="山田 太郎" division="業務部" department="業務Ⅰ課"
///               employment="正社員" pattern="ハウス" joined="2020-04-01" left="" managed="true"/&gt;
///   &lt;/employees&gt;
///
/// name(氏名)と division(所属部)は必須。division は所定労働時間マスタの引き当てキーになる。
/// managed(管理区分)は勤怠管理の対象にするか。省略した場合は対象(true)。
///
/// 未登録の社員は正社員として扱う(仕様書 Q-03 の確定待ち)。
/// </summary>
public sealed class EmployeeMaster
{
    /// <summary>読み込み時の注意・エラー。</summary>
    public List<string> Messages { get; } = new();

    private readonly Dictionary<string, EmployeeEntry> _byKey = new();

    /// <summary>マスタに登録が無い社員の既定の雇用区分。</summary>
    public EmploymentType DefaultEmployment { get; set; } = EmploymentType.FullTime;

    public int EntryCount => _byKey.Count;
    public IReadOnlyCollection<EmployeeEntry> All => _byKey.Values;

    public void Register(EmployeeEntry entry)
    {
        var key = NameNormalizer.Normalize(entry.CanonicalName);
        if (key.Length == 0) return;
        _byKey[key] = entry;
    }

    /// <summary>正規化済みの突合キーから1件を引く。未登録なら null。</summary>
    public EmployeeEntry? Find(string normalizedKey)
        => _byKey.TryGetValue(normalizedKey, out var e) ? e : null;

    /// <summary>雇用区分を解決する。未登録なら既定値。</summary>
    public EmploymentType ResolveEmployment(string normalizedKey)
        => Find(normalizedKey)?.Employment ?? DefaultEmployment;

    /// <summary>1日の拘束時間。未登録なら null(早退・時間外を判定しない)。</summary>
    public TimeSpan? WorkHoursOf(string normalizedKey) => Find(normalizedKey)?.WorkHours;

    /// <summary>基本時給。未登録なら null(給与計算表の時給欄は空のまま)。</summary>
    public int? HourlyWageOf(string normalizedKey) => Find(normalizedKey)?.HourlyWage;

    /// <summary>所属部。所定労働時間マスタの引き当てに使う。</summary>
    public string DivisionOf(string normalizedKey) => Find(normalizedKey)?.Division ?? "";

    /// <summary>
    /// 管理区分がオンの方か。マスタに登録が無い方はオン(従来どおり対象)として扱う。
    /// オフの方は突合結果の一覧・帳票のどちらにも出さない。
    /// </summary>
    public bool IsManaged(string normalizedKey) => Find(normalizedKey)?.IsManaged ?? true;

    /// <summary>管理区分がオフ(対象外)の登録件数。</summary>
    public int UnmanagedCount => _byKey.Values.Count(e => !e.IsManaged);

    /// <summary>処理ログに出す、管理区分の1行要約。</summary>
    public string ManagedSummary
        => UnmanagedCount == 0
            ? $"管理区分オン {EntryCount} 名(対象外の登録はありません)"
            : $"管理区分オン {EntryCount - UnmanagedCount} 名 / オフ(対象外) {UnmanagedCount} 名";

    /// <summary>処理ログに出す、パート・アルバイトの1行要約。</summary>
    public string PartTimeSummary
    {
        get
        {
            var partTimers = _byKey.Values.Where(e => e.Employment != EmploymentType.FullTime).ToList();
            if (partTimers.Count == 0) return "登録なし(全員を正社員として扱います)";
            return $"{partTimers.Count} 名 " +
                   $"(拘束時間の登録 {partTimers.Count(e => e.WorkHours != null)} 名 / " +
                   $"時給の登録 {partTimers.Count(e => e.HourlyWage != null)} 名)";
        }
    }

    public static EmployeeMaster Load(string? xmlPath)
    {
        var m = new EmployeeMaster();
        var root = MasterXml.LoadRoot(xmlPath, "従業員マスタ", m.Messages);
        if (root == null) return m;

        foreach (var e in root.Elements("employee"))
        {
            var name = e.Attr("name");
            if (name.Length == 0) continue;

            var employmentText = e.Attr("employment");
            if (!TryParseEmployment(employmentText, out var employment))
            {
                m.Messages.Add($"[W-MS-008] 従業員マスタ「{name}」の雇用区分 '{employmentText}' は認識できません。" +
                               "正社員 / パート / アルバイト のいずれかを指定してください。正社員として続行します。");
                employment = EmploymentType.FullTime;
            }

            var division = e.Attr("division");
            if (division.Length == 0)
                m.Messages.Add($"[W-MS-008] 従業員マスタ「{name}」に所属部(division)がありません。" +
                               "所定労働時間マスタを引き当てられないため、拘束時間で判定します。");

            m.Register(new EmployeeEntry
            {
                CanonicalName = name,
                EmployeeNo = e.Attr("no"),
                Division = division,
                Department = e.Attr("department"),
                Employment = employment,
                ShiftPattern = e.Attr("pattern"),
                WorkHours = ParseWorkHours(e.Attr("workHours"), name, m),
                HourlyWage = ParseWage(e.Attr("hourlyWage"), name, m),
                JoinedOn = ParseDate(e.Attr("joined")),
                LeftOn = ParseDate(e.Attr("left")),
                IsManaged = ParseManaged(e.Attr("managed"))
            });
        }
        return m;
    }

    /// <summary>「正社員」「パート」「アルバイト」および英字表記を読む。空欄は正社員。</summary>
    public static bool TryParseEmployment(string text, out EmploymentType value)
    {
        value = EmploymentType.FullTime;
        var s = text.Trim();
        if (s.Length == 0) return true;

        switch (s.ToLowerInvariant())
        {
            case "正社員": case "社員": case "fulltime": case "full-time":
                value = EmploymentType.FullTime; return true;
            case "パート": case "ﾊﾟｰﾄ": case "parttime": case "part-time":
                value = EmploymentType.PartTime; return true;
            case "アルバイト": case "ｱﾙﾊﾞｲﾄ": case "バイト": case "arbeit":
                value = EmploymentType.Arbeit; return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// 管理区分を読む。空欄は「対象」(既存のマスタに managed が無くても全員が対象のまま)。
    /// 「false / 0 / × / いいえ / 対象外」のときだけ対象外にする。
    /// </summary>
    public static bool ParseManaged(string text)
    {
        var s = text.Trim();
        if (s.Length == 0) return true;

        return s.ToLowerInvariant() switch
        {
            "false" or "0" or "no" or "off" or "×" or "x" or "いいえ" or "対象外" or "無" or "なし" => false,
            _ => true
        };
    }

    /// <summary>管理区分をXMLに書く文字。</summary>
    public static string ManagedText(bool value) => value ? "true" : "false";

    /// <summary>雇用区分の表示名。</summary>
    public static string Label(EmploymentType type) => type switch
    {
        EmploymentType.PartTime => "パート",
        EmploymentType.Arbeit => "アルバイト",
        _ => "正社員"
    };

    /// <summary>パート・アルバイト給与計算表の対象か(正社員のシートは生成しない)。</summary>
    public static bool IsPartTimePayroll(EmploymentType type) => type != EmploymentType.FullTime;

    private static DateOnly? ParseDate(string text)
        => DateOnly.TryParse(text.Trim(), out var d) ? d : null;

    /// <summary>「9:00」「9」を1日の拘束時間として読む。</summary>
    public static bool TryParseWorkHours(string text, out TimeSpan value)
    {
        value = default;
        var s = text.Trim();
        if (s.Length == 0) return false;

        if (!s.Contains(':'))
        {
            if (!double.TryParse(s, System.Globalization.NumberStyles.Float,
                                 System.Globalization.CultureInfo.InvariantCulture, out var hours)) return false;
            if (hours is <= 0 or > 24) return false;
            value = TimeSpan.FromMinutes(Math.Round(hours * 60));
            return true;
        }

        if (!Parsing.TimeText.TryParse(s, out var span) || span <= TimeSpan.Zero || span > TimeSpan.FromHours(24))
            return false;
        value = span;
        return true;
    }

    private static TimeSpan? ParseWorkHours(string text, string name, EmployeeMaster m)
    {
        if (text.Trim().Length == 0) return null;
        if (TryParseWorkHours(text, out var value)) return value;

        m.Messages.Add($"[W-MS-008] 従業員マスタ「{name}」の1日の拘束時間 '{text}' を読めません。" +
                       "「9:00」または「9」の形で書いてください。この指定は無視します。");
        return null;
    }

    private static int? ParseWage(string text, string name, EmployeeMaster m)
    {
        if (text.Trim().Length == 0) return null;
        if (int.TryParse(text.Trim(), out var value) && value is > 0 and < 100000) return value;

        m.Messages.Add($"[W-MS-008] 従業員マスタ「{name}」の時給 '{text}' を読めません。" +
                       "1〜99999 の数値で書いてください。空欄として扱います。");
        return null;
    }
}
