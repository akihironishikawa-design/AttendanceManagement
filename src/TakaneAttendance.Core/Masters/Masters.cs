using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Naming;

namespace TakaneAttendance.Core.Masters;

/// <summary>
/// マスタXMLの読み込みで共通に使う処理。
///
/// マスタは運用中にお客様が手で編集するため、書式の誤りで処理全体が落ちないようにする。
/// 読めなかった場合は例外を投げず、理由を <see cref="Messages"/> に残して既定値で続行する。
/// </summary>
public static class MasterXml
{
    /// <summary>
    /// マスタXMLを読む。ファイルが無い場合は null(マスタなしとして扱う)。
    /// 書式が壊れている場合も null を返し、理由を messages に積む。
    /// </summary>
    public static XElement? LoadRoot(string? path, string masterName, List<string> messages)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return XDocument.Load(path).Root;
        }
        catch (XmlException ex)
        {
            messages.Add($"[E-MS-001] {masterName}({Path.GetFileName(path)})のXMLを読めません: {ex.Message}");
            return null;
        }
        catch (IOException ex)
        {
            messages.Add($"[E-MS-002] {masterName}({Path.GetFileName(path)})を開けません: {ex.Message}");
            return null;
        }
    }

    /// <summary>属性値。未指定なら空文字。前後の空白は落とす。</summary>
    public static string Attr(this XElement e, string name) => ((string?)e.Attribute(name) ?? "").Trim();
}

/// <summary>
/// 社員名の別名マスタ。
///
/// シフト表には「平山 部長」「原田 課長」のように役職で書かれた氏名があり、
/// 打刻データの「平山 恵亮」「原田 祥吾」とは文字列が一致しない。
/// この対応表を持つことで、正規化だけでは解決できない氏名を正式氏名に寄せる。
///
/// XML書式(name_alias.xml):
///   &lt;nameAliases&gt;
///     &lt;alias source="平山 部長" canonical="平山 恵亮" note="備考"/&gt;
///   &lt;/nameAliases&gt;
///   canonical を省略した行は「正式氏名そのものの登録」として扱う。
///
/// 正式氏名側は、打刻データの読み込み時に自動登録される(RegisterCanonical)。
/// このため「表記ゆれが空白だけ」の氏名はマスタに書かなくても自動で解決する。
/// </summary>
public sealed class AliasMaster
{
    /// <summary>読み込み時の注意・エラー。</summary>
    public List<string> Messages { get; } = new();

    private readonly Dictionary<string, string> _aliasToCanonical = new();
    private readonly Dictionary<string, string> _canonicalByNormalized = new();

    public int AliasCount => _aliasToCanonical.Count;
    public int CanonicalCount => _canonicalByNormalized.Count;

    /// <summary>正式氏名を登録する(打刻データの社員一覧から自動登録される)。</summary>
    public void RegisterCanonical(string canonicalName)
    {
        if (string.IsNullOrWhiteSpace(canonicalName)) return;
        var key = NameNormalizer.Normalize(canonicalName);
        if (key.Length == 0) return;
        _canonicalByNormalized[key] = canonicalName.Trim();
    }

    /// <summary>別名を登録する。</summary>
    public void RegisterAlias(string alias, string canonicalName)
    {
        if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(canonicalName)) return;
        _aliasToCanonical[NameNormalizer.Normalize(alias)] = canonicalName.Trim();
        RegisterCanonical(canonicalName);
    }

    /// <summary>正規化済みの氏名を正式氏名へ解決する。解決できなければ null。</summary>
    public string? Resolve(string normalizedName)
    {
        if (string.IsNullOrEmpty(normalizedName)) return null;
        if (_aliasToCanonical.TryGetValue(normalizedName, out var byAlias)) return byAlias;
        if (_canonicalByNormalized.TryGetValue(normalizedName, out var byName)) return byName;
        return null;
    }

    public static AliasMaster Load(string? xmlPath)
    {
        var m = new AliasMaster();
        var root = MasterXml.LoadRoot(xmlPath, "社員名 別名マスタ", m.Messages);
        if (root == null) return m;

        foreach (var e in root.Elements("alias"))
        {
            var source = e.Attr("source");
            if (source.Length == 0) continue;

            var canonical = e.Attr("canonical");
            // 正式氏名が空の行は「正式氏名そのもの」の登録として扱う
            if (canonical.Length == 0) m.RegisterCanonical(source);
            else m.RegisterAlias(source, canonical);
        }
        return m;
    }

    /// <summary>未解決氏名を追記できる形の XML を生成する(画面からの書き出し用)。</summary>
    public static string BuildXmlTemplate(IEnumerable<UnresolvedName> unresolved)
    {
        var root = new XElement("nameAliases",
            new XComment(" 未解決だった氏名の下書き。canonical に正式氏名を記入して " +
                         "masters/name_alias.xml の <nameAliases> の中へ移してください。 "));

        foreach (var u in unresolved.OrderBy(u => u.Origin).ThenBy(u => u.SourceName))
            root.Add(new XElement("alias",
                new XAttribute("source", u.SourceName),
                new XAttribute("canonical", ""),
                new XAttribute("note", $"{u.Origin} {u.Occurrences}件 {u.Department}".Trim())));

        // XDocument.ToString() は宣言を出さないため、宣言だけ先頭に付ける
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + Environment.NewLine + root + Environment.NewLine;
    }
}

/// <summary>勤務区分マスタの1件。</summary>
/// <param name="Code">シフト表のセルに入る文字値(公・有・欠・出張 など)</param>
/// <param name="Kind">判定に使う種別</param>
/// <param name="Description">XML の description 属性</param>
public sealed record ShiftTypeEntry(string Code, ShiftKind Kind, string Description)
{
    /// <summary>画面に出す短い名称。備考の補足(かっこ書き)は落とす。</summary>
    public string DisplayName =>
        Description.Length == 0 ? Code : Description.Split('(', '(')[0].Trim();
}

/// <summary>
/// 勤務区分マスタ。シフト表のセルに入る文字値(公・有・欠・出張 など)の意味を定義する。
///
/// XML書式(shift_type.xml):
///   &lt;shiftTypes&gt;
///     &lt;shiftType code="公" kind="DayOff" description="公休"/&gt;
///   &lt;/shiftTypes&gt;
///   kind = Work / DayOff / PaidLeave / BusinessTrip / Other / Excluded
///   (旧版の Absence / HalfDay は Other として読み替える)
/// </summary>
public sealed class ShiftTypeMaster
{
    private readonly Dictionary<string, ShiftKind> _map = new();
    private readonly List<ShiftTypeEntry> _entries = new();

    /// <summary>読み込み時の注意・エラー。</summary>
    public List<string> Messages { get; } = new();

    public IReadOnlyDictionary<string, ShiftKind> Entries => _map;

    /// <summary>登録順の一覧。画面の区分選択に使う。</summary>
    public IReadOnlyList<ShiftTypeEntry> All => _entries;

    public ShiftKind Resolve(string code)
    {
        var key = NameNormalizer.Normalize(code);
        return _map.TryGetValue(key, out var kind) ? kind : ShiftKind.Unknown;
    }

    public void Register(string code, ShiftKind kind, string description = "")
    {
        var key = NameNormalizer.Normalize(code);
        _map[key] = kind;

        var entry = new ShiftTypeEntry(code.Trim(), kind, description.Trim());
        int index = _entries.FindIndex(e => NameNormalizer.Normalize(e.Code) == key);
        if (index >= 0) _entries[index] = entry;
        else _entries.Add(entry);
    }

    /// <summary>実データで確認できた区分を既定値として持つ。XMLがあれば上書き・追加される。</summary>
    public static ShiftTypeMaster CreateDefault()
    {
        var m = new ShiftTypeMaster();
        m.Register("公", ShiftKind.DayOff, "公休");
        m.Register("有", ShiftKind.PaidLeave, "有給");
        m.Register("出張", ShiftKind.BusinessTrip, "出張");
        m.Register("本部", ShiftKind.BusinessTrip, "本部勤務");
        m.Register("欠", ShiftKind.Other, "その他(欠勤)");
        m.Register("半", ShiftKind.Other, "その他(半休)");
        return m;
    }

    public static ShiftTypeMaster Load(string? xmlPath)
    {
        var m = CreateDefault();
        var root = MasterXml.LoadRoot(xmlPath, "勤務区分マスタ", m.Messages);
        if (root == null) return m;

        foreach (var e in root.Elements("shiftType"))
        {
            var code = e.Attr("code");
            if (code.Length == 0) continue;

            var kindText = e.Attr("kind");
            if (!TryParseKind(kindText, out var kind))
            {
                m.Messages.Add($"[W-MS-003] 勤務区分「{code}」の種別 '{kindText}' は認識できません。" +
                               "Work / DayOff / PaidLeave / BusinessTrip / Other / Excluded のいずれかを指定してください。");
                continue;
            }
            m.Register(code, kind, e.Attr("description"));
        }
        return m;
    }

    /// <summary>
    /// kind 属性を読む。旧版(詳細設計書 v2.0)の Absence / HalfDay は、
    /// 仕様書 v3.0 の6区分に合わせて Other として読み替える。
    /// </summary>
    private static bool TryParseKind(string text, out ShiftKind kind)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "absence":
            case "halfday":
                kind = ShiftKind.Other;
                return true;
            default:
                return Enum.TryParse(text, ignoreCase: true, out kind) && kind != ShiftKind.Unknown;
        }
    }
}

/// <summary>マスタ一式(XML)。既定では実行ファイル配下の masters フォルダから読み込む。</summary>
public sealed class MasterSet
{
    public const string AliasFileName = "name_alias.xml";
    public const string ShiftTypeFileName = "shift_type.xml";
    public const string WorkingHoursFileName = "working_hours.xml";
    public const string BreakRuleFileName = "break_rule.xml";
    public const string EmployeeFileName = "employee.xml";
    public const string JudgementRuleFileName = "judgement_rule.xml";
    public const string ApplicationFormFileName = "application_form.xml";
    public const string HolidayFileName = "holiday.xml";

    public required AliasMaster Alias { get; init; }
    public required ShiftTypeMaster ShiftTypes { get; init; }
    public required WorkingHoursMaster WorkingHours { get; init; }
    public required BreakRuleMaster BreakRules { get; init; }
    public required EmployeeMaster Employees { get; init; }
    public required JudgementRuleMaster JudgementRules { get; init; }
    public required ApplicationFormMaster ApplicationForms { get; init; }
    public required HolidayMaster Holidays { get; init; }
    public string? Directory { get; init; }

    /// <summary>各マスタの読み込みで出た注意・エラーをまとめたもの。</summary>
    public IEnumerable<string> Messages =>
        Alias.Messages
            .Concat(ShiftTypes.Messages)
            .Concat(WorkingHours.Messages)
            .Concat(BreakRules.Messages)
            .Concat(Employees.Messages)
            .Concat(JudgementRules.Messages)
            .Concat(ApplicationForms.Messages)
            .Concat(Holidays.Messages);

    public static MasterSet Load(string? directory)
    {
        string? P(string name) => directory == null ? null : Path.Combine(directory, name);

        return new MasterSet
        {
            Directory = directory,
            Alias = AliasMaster.Load(P(AliasFileName)),
            ShiftTypes = ShiftTypeMaster.Load(P(ShiftTypeFileName)),
            WorkingHours = WorkingHoursMaster.Load(P(WorkingHoursFileName)),
            BreakRules = BreakRuleMaster.Load(P(BreakRuleFileName)),
            Employees = EmployeeMaster.Load(P(EmployeeFileName)),
            JudgementRules = JudgementRuleMaster.Load(P(JudgementRuleFileName)),
            ApplicationForms = ApplicationFormMaster.Load(P(ApplicationFormFileName)),
            Holidays = HolidayMaster.Load(P(HolidayFileName))
        };
    }

    /// <summary>アプリの既定のマスタ配置場所(実行ファイルと同階層の masters)。</summary>
    public static string DefaultDirectory =>
        Path.Combine(AppContext.BaseDirectory, "masters");
}
