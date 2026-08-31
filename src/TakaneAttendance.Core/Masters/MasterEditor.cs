using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace TakaneAttendance.Core.Masters;

/// <summary>別名マスタの1行。XMLに書かれている値をそのまま持つ(画面編集用)。</summary>
public sealed record AliasEntry(string Source, string Canonical, string Note);

/// <summary>所定労働時間マスタの所属部1件(画面編集用)。休憩は、期間で上書きしない日に使う既定値。</summary>
public sealed record WorkingHoursDivisionEntry(string Name, string WeekdayBreak, string HolidayBreak);

/// <summary>
/// 所定労働時間マスタの期間1件(画面編集用)。値は書かれているまま文字で持つ。
/// <paramref name="WeekdayBreak"/> / <paramref name="HolidayBreak"/> はこの期間だけの休憩(分)で、
/// 空欄なら所属部の既定を使う。
/// </summary>
public sealed record WorkingHoursPeriodEntry(string Division, string From, string To,
                                             string Weekday, string Holiday,
                                             string WeekdayBreak, string HolidayBreak);

/// <summary>
/// 従業員マスタの1行(画面編集用)。氏名と所属部は必須。
/// <paramref name="Managed"/> は管理区分で、オンの方だけが画面と帳票の対象になる。
/// </summary>
public sealed record EmployeeEditEntry(string No, string Name, string Division, string Department,
                                       string Employment, string Pattern, string WorkHours,
                                       string HourlyWage, string Joined, string Left, bool Managed);

/// <summary>祝日マスタの1件(画面編集用)。日付は「月/日」。</summary>
public sealed record HolidayEntry(string Date, string Kind, string Note);

/// <summary>申請書マスタの1行(画面編集用)。判定コード → 必要な申請書。</summary>
public sealed record ApplicationFormEntry(string Code, string FormName, string Reason);

/// <summary>休憩ルールの段階1件(画面編集用)。上限を空欄にすると「それ以上すべて」。</summary>
public sealed record BreakBandEntry(string UpToHours, string BreakMinutes);

/// <summary>休憩ルールの丸め設定(画面編集用)。</summary>
public sealed record BreakRuleSettings(string UnitMinutes, string InRounding, string OutRounding);

/// <summary>判定閾値マスタ(画面編集用)。値はすべて分。</summary>
public sealed record JudgementRuleSettings(string EarlyInMinutes, string OvertimeMinutes,
                                           string FullTimeSpanMinutes, string ToleranceMinutes);

/// <summary>
/// マスタXMLを画面から修正するための読み書き。
///
/// マスタXMLにはお客様向けの説明コメントが書かれているため、保存時にファイルを作り直さない。
/// 元のコメント・ルート要素をそのまま残し、対象要素(alias / shiftType / pattern)だけを
/// 画面の内容で差し替える。上書きの前には同名 + .bak の控えを残す。
///
/// 読み込み側(<see cref="AliasMaster"/> など)は突合に必要な形へ変換して持つため、
/// note 属性や書式の誤りをそのまま画面に出すには、ここで別途XMLを読む。
/// </summary>
public static class MasterEditor
{
    /// <summary>上書き前の控えに付ける拡張子。</summary>
    public const string BackupExtension = ".bak";

    private const string AliasHeader =
        "\n  社員名 別名マスタ\n\n" +
        "  書式: <alias source=\"表記\" canonical=\"正式氏名\" note=\"備考\"/>\n" +
        "    ・source    = シフト表など、突合したい側に書かれている氏名\n" +
        "    ・canonical = タイムレコーダー(打刻データ)に登録されている氏名\n" +
        "    ・canonical を省略した行は「正式氏名そのものの登録」として扱われる\n";

    private const string ShiftTypeHeader =
        "\n  勤務区分マスタ\n\n" +
        "  書式: <shiftType code=\"区分\" kind=\"種別\" description=\"備考\"/>\n" +
        "    ・kind = Work / DayOff / PaidLeave / HalfDay / Absence / BusinessTrip\n";

    private const string WorkingHoursHeader =
        "\n  所定労働時間マスタ(所属部別・時期別)\n\n" +
        "  予定終了 = 予定開始 + 所定労働時間 + 休憩\n\n" +
        "  書式: <division name=\"所属部\" weekdayBreak=\"平日の休憩分\" holidayBreak=\"土日祝の休憩分\">\n" +
        "          <period from=\"4/1\" to=\"6/30\" weekday=\"8\" holiday=\"8.5\"\n" +
        "                  weekdayBreak=\"90\" holidayBreak=\"60\"/>\n" +
        "          <period from=\"4/1\" to=\"5/15\" hours=\"8\" breakMinutes=\"45\"/>\n" +
        "        </division>\n" +
        "    ・name は従業員マスタの division(所属部)と同じ文字で書く\n" +
        "    ・from / to は「月/日」。年をまたぐ範囲(10/16〜2/15)も書ける\n" +
        "    ・weekday = 平日 / holiday = 土日祝。同じ場合は hours 1つでよい\n" +
        "    ・休憩が平日・土日祝で同じ場合は breakMinutes 1つでよい\n" +
        "\n" +
        "  休憩は「所属部 × 期間 × 平日/土日祝」の3つで決まります。\n" +
        "    ・division の休憩 … その所属部の既定。期間で書いていない日に使う\n" +
        "    ・period  の休憩 … その期間だけの上書き。書かなければ division の値を使う\n";

    private const string EmployeeHeader =
        "\n  従業員マスタ\n\n" +
        "  書式: <employee no=\"社員番号\" name=\"氏名\" division=\"所属部\" department=\"所属課\"\n" +
        "                 employment=\"雇用区分\" pattern=\"部門(勤務地)\" joined=\"入社日\" left=\"退職日\"\n" +
        "                 managed=\"true\"/>\n" +
        "    ・name(氏名) と division(所属部) は必須\n" +
        "    ・managed  = 管理区分。true の方だけを突合結果の一覧と帳票に出します\n" +
        "                 (false にすると、シフト表・打刻データに載っていても対象外)\n" +
        "                 省略した場合は true(対象)として扱います\n" +
        "    ・division は所定労働時間マスタ(working_hours.xml)の引き当てキー\n" +
        "    ・employment = 正社員 / パート / アルバイト\n" +
        "    ・workHours  = 1日の拘束時間(休憩込み)。パート・アルバイトの予定終了 = 予定開始 + この時間\n" +
        "    ・hourlyWage = 基本時給(円)。パート・アルバイト給与計算表の時給欄\n" +
        "    ・import-employees コマンドで作り直すと、画面で直した内容は失われます\n";

    private const string HolidayHeader =
        "\n  祝日マスタ\n\n" +
        "  書式: <holiday date=\"7/20\" kind=\"祝\" note=\"海の日\"/>\n" +
        "    ・date は「月/日」。毎年同じ日で運用するため、年は持ちません\n" +
        "    ・kind = 祝 / 休場\n" +
        "    ・祝日は土日と同じ扱い(見出しが赤・所定労働時間は土日祝の値)\n" +
        "    ・休場日はその日を対象外(グレー表示)にします\n" +
        "    ・春分の日・秋分の日と第◯月曜日の祝日は年によって動くため、年度ごとに見直してください\n";

    private const string ApplicationFormHeader =
        "\n  申請書マスタ(判定 → 必要な申請書)\n\n" +
        "  書式: <form code=\"判定コード\" name=\"申請書名\" reason=\"理由\"/>\n" +
        "    ・code  = 判定コード(NO_PUNCH / LATE / PAID_LEAVE など)\n" +
        "    ・name  = タイムカード修正届出書 / 年次有休休暇・欠勤申請書 / 出張届 / 勤怠管理簿\n" +
        "    ・reason = 申請書の「理由」欄に出る文言\n" +
        "    ・1つの判定コードに複数の申請書を割り当てられます\n";

    private const string BreakRuleHeader =
        "\n  休憩ルールマスタ(給与計算表の実労働時間)\n\n" +
        "  書式: <breakRule unitMinutes=\"15\" inRounding=\"up\" outRounding=\"down\">\n" +
        "          <band upToHours=\"6\" breakMinutes=\"15\"/>\n" +
        "          <band breakMinutes=\"90\"/>\n" +
        "        </breakRule>\n" +
        "    ・unitMinutes = 打刻の丸めの単位(分)\n" +
        "    ・inRounding / outRounding = up(切り上げ) / down(切り捨て) / nearest(四捨五入)\n" +
        "    ・band は拘束時間の上限(時間)と、そのときの休憩(分)。上限を書かない行は「それ以上すべて」\n";

    private const string JudgementRuleHeader =
        "\n  判定閾値マスタ\n\n" +
        "  書式: <judgementRule earlyInMinutes=\"30\" overtimeMinutes=\"30\"\n" +
        "                       fullTimeSpanMinutes=\"570\" toleranceMinutes=\"0\"/>\n" +
        "    ・earlyInMinutes      = 早出とみなす分(予定開始より何分早いか)\n" +
        "    ・overtimeMinutes     = 時間外とみなす分(予定終了より何分遅いか)\n" +
        "    ・fullTimeSpanMinutes = 所定労働時間マスタに登録の無い所属部で使う拘束時間(分)\n" +
        "    ・toleranceMinutes    = 遅刻・早退の許容(分)。0 なら1分でも遅刻\n";

    // ================= 読み込み =================

    /// <summary>別名マスタをXMLに書かれているまま読む。ファイルが無ければ空の一覧。</summary>
    public static List<AliasEntry> LoadAliases(string path, List<string> messages)
    {
        var list = new List<AliasEntry>();
        var root = MasterXml.LoadRoot(path, "社員名 別名マスタ", messages);
        if (root == null) return list;

        foreach (var e in root.Elements("alias"))
        {
            var source = e.Attr("source");
            if (source.Length == 0) continue;
            list.Add(new AliasEntry(source, e.Attr("canonical"), e.Attr("note")));
        }
        return list;
    }

    /// <summary>
    /// 所定労働時間マスタをXMLに書かれているまま読む。
    /// グループ(部門のまとまり)と期間を、画面の2つの一覧に分けて返す。
    /// </summary>
    public static (List<WorkingHoursDivisionEntry> Divisions, List<WorkingHoursPeriodEntry> Periods)
        LoadWorkingHours(string path, List<string> messages)
    {
        var divisions = new List<WorkingHoursDivisionEntry>();
        var periods = new List<WorkingHoursPeriodEntry>();

        var root = MasterXml.LoadRoot(path, "所定労働時間マスタ", messages);
        if (root == null) return (divisions, periods);

        foreach (var g in root.Elements("division"))
        {
            var name = g.Attr("name");
            if (name.Length == 0) name = "(名称なし)";

            // breakMinutes は平日・土日祝の共通指定。個別指定があればそちらを出す
            var commonBreak = g.Attr("breakMinutes");
            var weekdayBreak = g.Attr("weekdayBreak");
            var holidayBreak = g.Attr("holidayBreak");
            divisions.Add(new WorkingHoursDivisionEntry(name,
                weekdayBreak.Length > 0 ? weekdayBreak : commonBreak,
                holidayBreak.Length > 0 ? holidayBreak : commonBreak));

            foreach (var p in g.Elements("period"))
            {
                // hours を書いた行は、平日・土日祝とも同じ時間になる
                var hours = p.Attr("hours");
                var weekday = p.Attr("weekday");
                var holiday = p.Attr("holiday");
                if (hours.Length > 0)
                {
                    if (weekday.Length == 0) weekday = hours;
                    if (holiday.Length == 0) holiday = hours;
                }
                // 休憩も breakMinutes が平日・土日祝の共通指定。個別指定があればそちらを出す
                var periodCommonBreak = p.Attr("breakMinutes");
                var periodWeekdayBreak = p.Attr("weekdayBreak");
                var periodHolidayBreak = p.Attr("holidayBreak");

                periods.Add(new WorkingHoursPeriodEntry(name, p.Attr("from"), p.Attr("to"), weekday, holiday,
                    periodWeekdayBreak.Length > 0 ? periodWeekdayBreak : periodCommonBreak,
                    periodHolidayBreak.Length > 0 ? periodHolidayBreak : periodCommonBreak));
            }
        }
        return (divisions, periods);
    }

    /// <summary>従業員マスタをXMLに書かれているまま読む。</summary>
    public static List<EmployeeEditEntry> LoadEmployees(string path, List<string> messages)
    {
        var list = new List<EmployeeEditEntry>();
        var root = MasterXml.LoadRoot(path, "従業員マスタ", messages);
        if (root == null) return list;

        foreach (var e in root.Elements("employee"))
        {
            var name = e.Attr("name");
            if (name.Length == 0) continue;
            list.Add(new EmployeeEditEntry(e.Attr("no"), name, e.Attr("division"), e.Attr("department"),
                                           e.Attr("employment"), e.Attr("pattern"), e.Attr("workHours"),
                                           e.Attr("hourlyWage"), e.Attr("joined"), e.Attr("left"),
                                           EmployeeMaster.ParseManaged(e.Attr("managed"))));
        }
        return list;
    }

    /// <summary>祝日マスタをXMLに書かれているまま読む。</summary>
    public static List<HolidayEntry> LoadHolidays(string path, List<string> messages)
    {
        var list = new List<HolidayEntry>();
        var root = MasterXml.LoadRoot(path, "祝日マスタ", messages);
        if (root == null) return list;

        foreach (var e in root.Elements("holiday"))
        {
            var date = e.Attr("date");
            if (date.Length == 0) continue;
            list.Add(new HolidayEntry(date, e.Attr("kind"), e.Attr("note")));
        }
        return list;
    }

    /// <summary>申請書マスタをXMLに書かれているまま読む。</summary>
    public static List<ApplicationFormEntry> LoadApplicationForms(string path, List<string> messages)
    {
        var list = new List<ApplicationFormEntry>();
        var root = MasterXml.LoadRoot(path, "申請書マスタ", messages);
        if (root == null) return list;

        foreach (var e in root.Elements("form"))
        {
            var code = e.Attr("code");
            if (code.Length == 0) continue;
            list.Add(new ApplicationFormEntry(code, e.Attr("name"), e.Attr("reason")));
        }
        return list;
    }

    /// <summary>休憩ルールマスタをXMLに書かれているまま読む。丸め設定は root 属性から。</summary>
    public static List<BreakBandEntry> LoadBreakRule(string path, List<string> messages, out BreakRuleSettings settings)
    {
        settings = new BreakRuleSettings("15", "up", "down");
        var list = new List<BreakBandEntry>();
        var root = MasterXml.LoadRoot(path, "休憩ルールマスタ", messages);
        if (root == null) return list;

        settings = new BreakRuleSettings(
            root.Attr("unitMinutes").Length > 0 ? root.Attr("unitMinutes") : "15",
            root.Attr("inRounding").Length > 0 ? root.Attr("inRounding") : "up",
            root.Attr("outRounding").Length > 0 ? root.Attr("outRounding") : "down");

        foreach (var e in root.Elements("band"))
            list.Add(new BreakBandEntry(e.Attr("upToHours"), e.Attr("breakMinutes")));
        return list;
    }

    /// <summary>判定閾値マスタをXMLに書かれているまま読む(値は root の属性)。</summary>
    public static JudgementRuleSettings LoadJudgementRule(string path, List<string> messages)
    {
        var fallback = new JudgementRuleSettings("30", "30", "570", "0");
        var root = MasterXml.LoadRoot(path, "判定閾値マスタ", messages);
        if (root == null) return fallback;

        string Or(string name, string alt) => root.Attr(name).Length > 0 ? root.Attr(name) : alt;
        return new JudgementRuleSettings(
            Or("earlyInMinutes", "30"), Or("overtimeMinutes", "30"),
            Or("fullTimeSpanMinutes", "570"), Or("toleranceMinutes", "0"));
    }

    // ================= 保存 =================

    /// <summary>
    /// 別名を1件だけ登録する(氏名未解決の一覧から直接登録するときに使う)。
    /// 同じ表記が既にある場合は、その行の正式氏名を書き換える。
    ///
    /// XMLが壊れていると既存の内容を失うため、読めない場合は登録せず例外にする。
    /// </summary>
    public static void UpsertAlias(string path, AliasEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.Source))
            throw new ArgumentException("表記が空です。", nameof(entry));

        var messages = new List<string>();
        var entries = LoadAliases(path, messages);
        if (messages.Count > 0)
            throw new InvalidOperationException(
                string.Join(Environment.NewLine, messages) +
                Environment.NewLine + "「マスタを編集」から内容を確認してください。");

        var key = Naming.NameNormalizer.Normalize(entry.Source);
        int index = entries.FindIndex(a => Naming.NameNormalizer.Normalize(a.Source) == key);
        if (index >= 0) entries[index] = entry;
        else entries.Add(entry);

        SaveAliases(path, entries);
    }

    public static void SaveAliases(string path, IEnumerable<AliasEntry> entries) =>
        Save(path, "nameAliases", "alias", AliasHeader,
            entries.Select(a => Element("alias",
                ("source", a.Source), ("canonical", a.Canonical), ("note", a.Note))));

    public static void SaveShiftTypes(string path, IEnumerable<ShiftTypeEntry> entries) =>
        Save(path, "shiftTypes", "shiftType", ShiftTypeHeader,
            entries.Select(s => Element("shiftType",
                ("code", s.Code), ("kind", s.Kind.ToString()), ("description", s.Description))));

    /// <summary>
    /// 所定労働時間マスタを保存する。
    /// 期間は「所属部」の名前で結び付ける。平日と土日祝が同じ行は hours 1つにまとめて書く。
    /// </summary>
    public static void SaveWorkingHours(string path, IEnumerable<WorkingHoursDivisionEntry> divisions,
                                        IEnumerable<WorkingHoursPeriodEntry> periods)
    {
        var byDivision = periods.ToLookup(p => p.Division.Trim());

        var elements = divisions.Select(g =>
        {
            // 平日と土日祝が同じなら1つにまとめて書く
            var element = g.WeekdayBreak.Trim() == g.HolidayBreak.Trim()
                ? Element("division", ("name", g.Name), ("breakMinutes", g.WeekdayBreak))
                : Element("division", ("name", g.Name),
                          ("weekdayBreak", g.WeekdayBreak), ("holidayBreak", g.HolidayBreak));

            foreach (var p in byDivision[g.Name.Trim()])
            {
                var weekday = p.Weekday.Trim();
                var holiday = p.Holiday.Trim();
                var period = weekday == holiday || holiday.Length == 0
                    ? Element("period", ("from", p.From), ("to", p.To), ("hours", weekday))
                    : Element("period", ("from", p.From), ("to", p.To), ("weekday", weekday), ("holiday", holiday));

                // 休憩は空欄なら書かない(所属部の既定を使う)。平日と土日祝が同じなら1つにまとめる
                var weekdayBreak = p.WeekdayBreak.Trim();
                var holidayBreak = p.HolidayBreak.Trim();
                if (weekdayBreak.Length > 0 && weekdayBreak == holidayBreak)
                    period.SetAttributeValue("breakMinutes", weekdayBreak);
                else
                {
                    if (weekdayBreak.Length > 0) period.SetAttributeValue("weekdayBreak", weekdayBreak);
                    if (holidayBreak.Length > 0) period.SetAttributeValue("holidayBreak", holidayBreak);
                }

                element.Add(period);
            }
            return element;
        });

        Save(path, "workingHours", "division", WorkingHoursHeader, elements);
    }

    /// <summary>
    /// 従業員マスタを保存する。空欄の属性は書かない(氏名と所属部は必須)。
    /// 管理区分だけは、省略時の既定(対象)と区別が付くよう true / false を必ず書く。
    /// </summary>
    public static void SaveEmployees(string path, IEnumerable<EmployeeEditEntry> entries) =>
        Save(path, "employees", "employee", EmployeeHeader,
            entries.Select(e => Element("employee",
                ("no", e.No), ("name", e.Name), ("division", e.Division), ("department", e.Department),
                ("employment", e.Employment), ("pattern", e.Pattern),
                ("workHours", e.WorkHours), ("hourlyWage", e.HourlyWage),
                ("joined", e.Joined), ("left", e.Left),
                ("managed", EmployeeMaster.ManagedText(e.Managed)))));

    /// <summary>祝日マスタを保存する。月/日の順に並べ替えて書く。</summary>
    public static void SaveHolidays(string path, IEnumerable<HolidayEntry> entries) =>
        Save(path, "holidays", "holiday", HolidayHeader,
            entries.OrderBy(d => HolidayMaster.TryParseMonthDay(d.Date, out var md) ? md : int.MaxValue)
                   .Select(d => Element("holiday", ("date", d.Date), ("kind", d.Kind), ("note", d.Note))));

    /// <summary>申請書マスタを保存する。</summary>
    public static void SaveApplicationForms(string path, IEnumerable<ApplicationFormEntry> entries) =>
        Save(path, "applicationForms", "form", ApplicationFormHeader,
            entries.Select(f => Element("form",
                ("code", f.Code), ("name", f.FormName), ("reason", f.Reason))));

    /// <summary>休憩ルールマスタを保存する。丸めの設定は root の属性に書く。</summary>
    public static void SaveBreakRule(string path, IEnumerable<BreakBandEntry> bands, BreakRuleSettings settings) =>
        Save(path, "breakRule", "band", BreakRuleHeader,
            bands.Select(b => Element("band", ("upToHours", b.UpToHours), ("breakMinutes", b.BreakMinutes))),
            root =>
            {
                root.SetAttributeValue("unitMinutes", settings.UnitMinutes.Trim());
                root.SetAttributeValue("inRounding", settings.InRounding.Trim());
                root.SetAttributeValue("outRounding", settings.OutRounding.Trim());
            });

    /// <summary>判定閾値マスタを保存する(値は root の属性のみ)。</summary>
    public static void SaveJudgementRule(string path, JudgementRuleSettings settings) =>
        Save(path, "judgementRule", "rule", JudgementRuleHeader, Enumerable.Empty<XElement>(),
            root =>
            {
                root.SetAttributeValue("earlyInMinutes", settings.EarlyInMinutes.Trim());
                root.SetAttributeValue("overtimeMinutes", settings.OvertimeMinutes.Trim());
                root.SetAttributeValue("fullTimeSpanMinutes", settings.FullTimeSpanMinutes.Trim());
                root.SetAttributeValue("toleranceMinutes", settings.ToleranceMinutes.Trim());
            });

    /// <summary>「業務部, 総務部」「業務部、総務部」のどちらの区切りでも受け付ける。</summary>
    public static IEnumerable<string> SplitDepartments(string text)
        => text.Split(new[] { ',', '、', ';', '/' }, StringSplitOptions.RemoveEmptyEntries)
               .Select(d => d.Trim())
               .Where(d => d.Length > 0);

    /// <summary>
    /// 対象要素だけを差し替えて保存する。
    /// 元ファイルのコメントは、最初の対象要素があった位置を基準に前後の並びを保って残す。
    /// </summary>
    private static void Save(string path, string rootName, string entryName, string headerComment,
                             IEnumerable<XElement> entries, Action<XElement>? updateRoot = null)
    {
        var doc = LoadForEdit(path, rootName, headerComment);
        var root = doc.Root!;

        // 画面の内容を最初の要素があった位置に入れてから、元の要素を取り除く。
        // こうすると、要素の前後に書かれたコメントの並びが元のまま残る。
        var existing = root.Elements(entryName).ToList();
        if (existing.Count > 0) existing[0].AddBeforeSelf(entries);
        else root.Add(entries);
        foreach (var e in existing) e.Remove();

        updateRoot?.Invoke(root);

        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        Backup(path);
        Write(doc, path);
    }

    /// <summary>
    /// 編集の下地にするXMLを用意する。
    /// ファイルが無い場合と、XMLとして読めない場合は説明コメント付きの空のマスタから作り直す
    /// (読めなかった内容は上書き時に .bak へ退避される)。
    /// </summary>
    private static XDocument LoadForEdit(string path, string rootName, string headerComment)
    {
        if (File.Exists(path))
        {
            try
            {
                var doc = XDocument.Load(path);
                if (doc.Root != null)
                {
                    doc.Declaration = new XDeclaration("1.0", "utf-8", null);
                    return doc;
                }
            }
            catch (XmlException) { /* 読めないファイルは作り直す */ }
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XComment(headerComment.Replace("\n", Environment.NewLine)),
            new XElement(rootName));
    }

    /// <summary>値が空の属性は書かない(マスタの見通しを悪くしないため)。</summary>
    private static XElement Element(string name, params (string Name, string Value)[] attributes)
    {
        var element = new XElement(name);
        foreach (var (attrName, value) in attributes)
            if (!string.IsNullOrWhiteSpace(value)) element.SetAttributeValue(attrName, value.Trim());
        return element;
    }

    /// <summary>上書きする前の内容を .bak として残す(手で編集した内容を取り戻せるようにする)。</summary>
    private static void Backup(string path)
    {
        if (!File.Exists(path)) return;
        try
        {
            File.Copy(path, path + BackupExtension, overwrite: true);
        }
        catch (IOException)
        {
            // 控えを作れなくても保存は続ける(退避先が使用中の場合など)
        }
    }

    /// <summary>BOM無しUTF-8・2スペース字下げで書き出す(元のマスタXMLと同じ形式)。</summary>
    private static void Write(XDocument doc, string path)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
        };
        using var writer = XmlWriter.Create(path, settings);
        doc.Save(writer);
    }
}
