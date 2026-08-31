using System.Xml.Linq;

namespace TakaneAttendance.Core.Masters;

/// <summary>日付の区分(統合仕様書 v3.0 第15章・第17章)。</summary>
public enum DayKind
{
    /// <summary>平日</summary>
    Weekday,
    /// <summary>土曜(見出しを青文字にする)</summary>
    Saturday,
    /// <summary>日曜(見出しを赤文字にする)</summary>
    Sunday,
    /// <summary>祝日(見出しを赤文字にする)</summary>
    Holiday,
    /// <summary>休場日(4行すべてグレー背景・対象外)</summary>
    Closed
}

/// <summary>
/// 祝日マスタ(統合仕様書 v3.0 第15章・第17章、要確認 Q-05)。
///
/// 祝日は曜日から求められないため、ここに登録する。
/// 毎年同じ日で運用するため、年は持たず「月/日」だけで登録する
/// (2026年も2027年も 7/20 は祝日、という持ち方)。
/// 休場日も同じ表に「月/日」で登録でき、その日は「対象外」としてグレー表示する。
///
/// XML書式(holiday.xml):
///   &lt;holidays&gt;
///     &lt;holiday date="7/20" kind="祝"   note="海の日"/&gt;
///     &lt;holiday date="7/6"  kind="休場" note="コース整備"/&gt;
///   &lt;/holidays&gt;
///
/// 登録の無い日は曜日から平日・土曜・日曜を判定する。
/// </summary>
public sealed class HolidayMaster
{
    /// <summary>読み込み時の注意・エラー。</summary>
    public List<string> Messages { get; } = new();

    /// <summary>キーは 月*100+日(例 7/20 → 720)。年は持たない。</summary>
    private readonly Dictionary<int, DayKind> _days = new();

    /// <summary>祝日の名称(海の日 など)。判定には使わず、マスタの見出しとして持つ。</summary>
    private readonly Dictionary<int, string> _notes = new();

    public int EntryCount => _days.Count;
    public int HolidayCount => _days.Count(d => d.Value == DayKind.Holiday);
    public int ClosedCount => _days.Count(d => d.Value == DayKind.Closed);

    /// <summary>処理ログに出す1行の要約。</summary>
    public string Summary => _days.Count == 0
        ? "祝日マスタなし(祝日は曜日から判定できないため平日として扱います)"
        : $"登録 {_days.Count} 日 (祝日 {HolidayCount} / 休場日 {ClosedCount})";

    /// <summary>月/日 で登録する(月*100+日)。</summary>
    public void Register(int monthDay, DayKind kind, string note = "")
    {
        _days[monthDay] = kind;
        if (note.Length > 0) _notes[monthDay] = note;
    }

    /// <summary>日付から登録する(年は捨てて月/日だけを見る)。</summary>
    public void Register(DateOnly date, DayKind kind, string note = "")
        => Register(date.Month * 100 + date.Day, kind, note);

    /// <summary>その月/日の名称。無ければ空。</summary>
    public string NoteOf(int monthDay) => _notes.TryGetValue(monthDay, out var note) ? note : "";

    /// <summary>登録されている月/日の一覧(名称の引き継ぎに使う)。</summary>
    public IEnumerable<int> MonthDays => _days.Keys;

    /// <summary>その月/日の区分。登録が無ければ平日。</summary>
    public DayKind KindOf(int monthDay) => _days.TryGetValue(monthDay, out var kind) ? kind : DayKind.Weekday;

    /// <summary>
    /// その日の区分を求める。
    /// 祝日マスタに同じ月日の登録があればそれを優先し、無ければ曜日から判定する。
    /// </summary>
    public DayKind Resolve(DateOnly date)
    {
        if (_days.TryGetValue(date.Month * 100 + date.Day, out var kind)) return kind;
        return date.DayOfWeek switch
        {
            DayOfWeek.Sunday => DayKind.Sunday,
            DayOfWeek.Saturday => DayKind.Saturday,
            _ => DayKind.Weekday
        };
    }

    /// <summary>休場日か(その日は対象外として扱う)。</summary>
    public bool IsClosed(DateOnly date) => Resolve(date) == DayKind.Closed;

    /// <summary>
    /// 土日祝か。所定労働時間の「土日祝」の値と、
    /// パート・アルバイト給与計算表の土日祝労働時間の集計に使う。
    /// </summary>
    public bool IsWeekendOrHoliday(DateOnly date)
        => Resolve(date) is DayKind.Saturday or DayKind.Sunday or DayKind.Holiday;

    /// <summary>見出しの文字色を決める区分名(土=青 / 日・祝=赤)。</summary>
    public string HeaderToneOf(DateOnly date) => Resolve(date) switch
    {
        DayKind.Saturday => "土",
        DayKind.Sunday or DayKind.Holiday => "日",
        _ => "平"
    };

    public static HolidayMaster Load(string? xmlPath)
    {
        var m = new HolidayMaster();
        var root = MasterXml.LoadRoot(xmlPath, "祝日マスタ", m.Messages);
        if (root == null) return m;

        foreach (var e in root.Elements("holiday"))
        {
            var text = e.Attr("date");
            if (!TryParseMonthDay(text, out var monthDay))
            {
                m.Messages.Add($"[W-MS-011] 祝日マスタの日付 '{text}' を読めません。" +
                               "「月/日」の形(例 7/20)で書いてください。この行は無視します。");
                continue;
            }

            if (!TryParseKind(e.Attr("kind"), out var kind))
            {
                m.Messages.Add($"[W-MS-011] 祝日マスタ {text} の区分 '{e.Attr("kind")}' は認識できません。" +
                               "祝 / 休場 のいずれかを指定してください。");
                continue;
            }
            m.Register(monthDay, kind, e.Attr("note"));
        }
        return m;
    }

    /// <summary>「7/20」「07/20」を 月*100+日 に直す(画面の入力チェックからも使う)。</summary>
    public static bool TryParseMonthDay(string text, out int monthDay)
    {
        monthDay = 0;
        var parts = text.Trim().Split('/', '-');

        // 「2026-07-20」のように年が付いていても、月/日だけを読む
        if (parts.Length == 3) parts = new[] { parts[1], parts[2] };
        if (parts.Length != 2) return false;

        if (!int.TryParse(parts[0], out var month) || !int.TryParse(parts[1], out var day)) return false;
        if (month is < 1 or > 12 || day is < 1 or > 31) return false;
        monthDay = month * 100 + day;
        return true;
    }

    /// <summary>月*100+日 を「7/20」の表記に直す。</summary>
    public static string FormatMonthDay(int monthDay) => $"{monthDay / 100}/{monthDay % 100}";

    /// <summary>取り込んだ内容を XML に書き出す(祝日取込コマンド用)。</summary>
    public string ToXml(string sourceDescription)
    {
        var comment = $@"
  祝日マスタ (統合仕様書 v3.0 第15章・第17章)

  このファイルは import-holidays コマンドで自動生成しています。
  手で編集した内容は再生成で失われます。画面の「マスタを編集」→「祝日」からも直せます。

    取込元 : {sourceDescription}

  毎年同じ日で運用するため、年は持たず「月/日」だけで登録します。

  用途
    ・祝  … 一覧見出しを赤文字にする。所定労働時間は「土日祝」の値を使う
    ・休場 … その日を「対象外」としてグレー表示する

  登録の無い日は曜日から平日・土曜・日曜を判定します。

  【要確認】年によって日が動く祝日
    春分の日・秋分の日と、第◯月曜日の祝日(成人の日・海の日・敬老の日・スポーツの日)は
    年によって日付が変わります。年度が変わったら、この表の見直しをお願いします。
  ";

        var root = new XElement("holidays", new XComment(comment));
        foreach (var (monthDay, kind) in _days.OrderBy(d => d.Key))
        {
            // 曜日から判定できる平日・土・日は書かない(祝日と休場日だけを持つ)
            if (kind is DayKind.Weekday or DayKind.Saturday or DayKind.Sunday) continue;
            var holiday = new XElement("holiday",
                new XAttribute("date", FormatMonthDay(monthDay)),
                new XAttribute("kind", Label(kind)));
            if (NoteOf(monthDay) is { Length: > 0 } note) holiday.Add(new XAttribute("note", note));
            root.Add(holiday);
        }

        return new XDocument(new XDeclaration("1.0", "utf-8", null), root).ToString();
    }

    public static string Label(DayKind kind) => kind switch
    {
        DayKind.Holiday => "祝",
        DayKind.Closed => "休場",
        DayKind.Saturday => "土",
        DayKind.Sunday => "日",
        _ => "平日"
    };

    public static bool TryParseKind(string text, out DayKind kind)
    {
        switch (text.Trim())
        {
            case "": case "祝": case "祝日": kind = DayKind.Holiday; return true;
            case "休場": case "休場日": case "休": kind = DayKind.Closed; return true;
            default: kind = DayKind.Holiday; return false;
        }
    }
}
