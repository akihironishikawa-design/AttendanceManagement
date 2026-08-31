namespace TakaneAttendance.Core.Reporting;

/// <summary>
/// 出席記録レポートの中身を、テンプレートのレイアウトのまま保持する編集可能なモデル。
///
/// 突合結果 → <see cref="ReportSheetBuilder"/> → このモデル → <see cref="AttendanceReportWriter"/> → .xlsx
/// という流れにすることで、画面に表示したものと出力される帳票が必ず同じ内容になる。
/// 画面上の編集はこのモデルへの書き込みとして反映される。
/// </summary>
public sealed class ReportSheet
{
    public required int Year { get; init; }
    public required int Month { get; init; }
    /// <summary>対象月の日数(=日列の数)</summary>
    public required int DayCount { get; init; }

    /// <summary>社員ブロック。並び順はシフト表の記載順(既定)、またはテンプレートの社員順。</summary>
    public List<ReportEmployeeBlock> Employees { get; } = new();

    /// <summary>組み立て時の注意メッセージ</summary>
    public List<string> Messages { get; } = new();

    /// <summary>
    /// 祝日マスタ。祝日の見出し色と、休場日の対象外表示に使う。
    /// 未設定の場合は曜日だけで平日・土曜・日曜を判定する。
    /// </summary>
    public Masters.HolidayMaster Holidays { get; set; } = new();

    /// <summary>画面での修正履歴(仕様書 v3.0 第18.1章)。</summary>
    public EditHistory History { get; } = new();

    /// <summary>日(1始まり)の曜日文字。</summary>
    public string DayOfWeekText(int day) =>
        DateOf(day) is not { } d ? "" : d.DayOfWeek switch
        {
            DayOfWeek.Sunday => "日", DayOfWeek.Monday => "月", DayOfWeek.Tuesday => "火",
            DayOfWeek.Wednesday => "水", DayOfWeek.Thursday => "木", DayOfWeek.Friday => "金", _ => "土"
        };

    /// <summary>土日かどうか(帳票の網掛けと画面の色分けに使う)。</summary>
    public bool IsWeekend(int day) =>
        DateOf(day) is { } d && d.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

    /// <summary>その日の区分(祝日・休場日を含む)。祝日マスタに無ければ曜日から判定する。</summary>
    public Masters.DayKind KindOf(int day)
        => DateOf(day) is { } d ? Holidays.Resolve(d) : Masters.DayKind.Weekday;

    /// <summary>見出しの文字色(平=黒 / 土=青 / 日・祝=赤)。仕様書 v3.0 第15.2章。</summary>
    public string HeaderToneOf(int day) => KindOf(day) switch
    {
        Masters.DayKind.Saturday => "土",
        Masters.DayKind.Sunday or Masters.DayKind.Holiday => "日",
        _ => "平"
    };

    /// <summary>休場日(4行すべてグレー背景・対象外)。</summary>
    public bool IsClosed(int day) => KindOf(day) == Masters.DayKind.Closed;

    /// <summary>日(1始まり)の日付。</summary>
    public DateOnly? DateOfDay(int day) => DateOf(day);

    private DateOnly? DateOf(int day)
    {
        if (Year == 0 || DayCount <= 0) return null;
        return new DateOnly(Year, Month, Math.Clamp(day, 1, DayCount));
    }

    public int EditedCellCount => Employees.Sum(e => e.EditedCellCount);
    /// <summary>要確認以上の判定が付いているセル数。</summary>
    public int AttentionCellCount => Employees.Sum(e => e.AttentionCount);

    /// <summary>
    /// 保留していた編集内容を、突合し直した帳票に重ねる。
    ///
    /// 保留を開くと元のシフト表・打刻データを読み直して突合するため、この帳票は
    /// 「今のファイルとマスタで作った、編集前の状態」になっている。そこへ保留ファイルの
    /// 編集(シフトの修正・予定終了時刻・備考・修正履歴)だけを重ね、最後に判定し直す。
    ///
    /// 打刻はタイムレコーダーの原値で編集できないため重ねない。
    /// 保留のあとに元ファイルから消えた社員は <see cref="DraftMergeResult.MissingEmployees"/> に入る。
    /// </summary>
    public DraftMergeResult ApplyDraftEdits(ReportSheet draft, ReportJudge? judge)
    {
        var byKey = new Dictionary<string, ReportEmployeeBlock>();
        foreach (var e in Employees)
            if (e.Key.Length > 0) byKey.TryAdd(e.Key, e);

        var applied = new DraftMergeResult();
        int days = Math.Min(DayCount, draft.DayCount);

        foreach (var d in draft.Employees)
        {
            if (!byKey.TryGetValue(d.Key, out var target))
            {
                // 編集が入っていた社員が今回の突合に居ない場合だけ知らせる(空欄の行は黙って捨てる)
                if (d.EditedCellCount > 0 || d.Note.Any(n => n.Length > 0))
                    applied.MissingEmployees.Add(d.Name.Length > 0 ? d.Name : d.Key);
                continue;
            }

            for (int i = 0; i < days; i++)
            {
                if (d.ShiftEdited[i])
                {
                    target.Shift[i] = d.Shift[i];
                    target.ShiftEdited[i] = true;
                    applied.EditedCells++;
                }
                // 申請書の提出を確認して直した打刻(仕様書 v3.0 第8.3章)
                if (d.PunchEdited[i])
                {
                    target.Punch[i] = d.Punch[i];
                    target.PunchEdited[i] = true;
                    applied.EditedCells++;
                }
                if (d.Note[i].Length > 0 && d.Note[i] != target.Note[i])
                {
                    target.Note[i] = d.Note[i];
                    applied.Notes++;
                }
                // 予定終了は突合でも埋まるため、値が違うものだけを画面での修正とみなす
                if (d.PlannedEnd[i].Length > 0 && d.PlannedEnd[i] != target.PlannedEnd[i])
                {
                    target.PlannedEnd[i] = d.PlannedEnd[i];
                    applied.PlannedEnds++;
                }
            }

            if (d.MetaEdited)
            {
                target.EmployeeNo = d.EmployeeNo;
                target.Name = d.Name;
                target.Department = d.Department;
                target.MetaEdited = true;
                applied.EditedCells++;
            }
        }

        History.Restore(draft.History.Entries);

        // 重ねた内容で判定し直す
        if (judge != null && Year > 0)
        {
            foreach (var b in Employees)
            {
                if (b.Person == null) continue;
                for (int i = 0; i < DayCount; i++)
                    b.Judgements[i] = judge.Evaluate(b.Person, new DateOnly(Year, Month, i + 1),
                                                     b.Shift[i], b.Punch[i], b.PlannedEnd[i]);
            }
        }

        return applied;
    }
}

/// <summary>保留していた編集を重ねた結果(処理ログに出す)。</summary>
public sealed class DraftMergeResult
{
    /// <summary>重ねたシフトの修正セル数。</summary>
    public int EditedCells { get; set; }
    /// <summary>重ねた備考の数。</summary>
    public int Notes { get; set; }
    /// <summary>重ねた予定終了時刻の数(画面で直した分)。</summary>
    public int PlannedEnds { get; set; }
    /// <summary>編集が入っていたのに、今回の突合に居なかった社員。</summary>
    public List<string> MissingEmployees { get; } = new();

    public bool IsEmpty => EditedCells == 0 && Notes == 0 && PlannedEnds == 0 && MissingEmployees.Count == 0;

    public string Describe()
    {
        var parts = new List<string>();
        if (EditedCells > 0) parts.Add($"シフトの修正 {EditedCells} セル");
        if (PlannedEnds > 0) parts.Add($"予定終了時刻 {PlannedEnds} セル");
        if (Notes > 0) parts.Add($"備考 {Notes} 件");
        return parts.Count == 0 ? "重ねる編集はありませんでした" : string.Join(" / ", parts) + " を重ねました";
    }
}

/// <summary>出席記録レポートの社員1件分(メタ行・シフト行・打刻行)。</summary>
public sealed class ReportEmployeeBlock
{
    public ReportEmployeeBlock(int dayCount)
    {
        Shift = new string[dayCount];
        Punch = new string[dayCount];
        PlannedEnd = new string[dayCount];
        Note = new string[dayCount];
        ShiftEdited = new bool[dayCount];
        PunchEdited = new bool[dayCount];
        Judgements = new CellJudgement[dayCount];
        Array.Fill(Shift, "");
        Array.Fill(Punch, "");
        Array.Fill(PlannedEnd, "");
        Array.Fill(Note, "");
        Array.Fill(Judgements, CellJudgement.None);
    }

    /// <summary>突合に使った社員情報。画面で編集したセルを判定し直すときに使う。</summary>
    public Models.PersonRef? Person { get; init; }

    public string EmployeeNo { get; set; } = "";
    public string Name { get; set; } = "";
    public string Department { get; set; } = "";

    /// <summary>突合キー(正規化氏名)。テンプレートとデータの突き合わせに使う。</summary>
    public string Key { get; init; } = "";

    /// <summary>今回の突合結果があった社員か。無い場合は行を残して空欄で出力する。</summary>
    public bool HasMatchingData { get; init; }

    /// <summary>テンプレートに無く、データ側にだけいたため末尾に追加した社員か。</summary>
    public bool AddedFromData { get; init; }

    /// <summary>シフト行。添字0 = 1日。勤務区分の文字値(公・有・出張 など)。</summary>
    public string[] Shift { get; }

    /// <summary>打刻行。添字0 = 1日。打刻の原文(丸めない)。</summary>
    public string[] Punch { get; }

    /// <summary>
    /// 予定終了時刻。添字0 = 1日。画面で修正でき、早退・時間外の判定に使う。
    /// 現行様式の帳票には出力しない(仕様書 v3.0 第8.3章)。
    /// </summary>
    public string[] PlannedEnd { get; }

    /// <summary>備考。修正理由・申請書の確認結果を残す(仕様書 v3.0 第8.3章)。</summary>
    public string[] Note { get; }

    /// <summary>画面で編集されたセル(添字はシフト行・打刻行それぞれの日)。</summary>
    public bool[] ShiftEdited { get; }
    /// <summary>打刻は読取専用のため常に false。保留ファイルの互換のために残している。</summary>
    public bool[] PunchEdited { get; }

    /// <summary>1日ごとの判定(シフトと打刻の矛盾)。添字0 = 1日。</summary>
    public CellJudgement[] Judgements { get; }

    public bool MetaEdited { get; set; }

    public int EditedCellCount =>
        ShiftEdited.Count(x => x) + PunchEdited.Count(x => x) + (MetaEdited ? 1 : 0);

    /// <summary>1日分でも値が入っているか(出力件数の集計に使う)。</summary>
    public bool HasAnyValue => Shift.Any(v => v.Length > 0) || Punch.Any(v => v.Length > 0);

    /// <summary>要確認以上の判定が付いている日数。</summary>
    public int AttentionCount => Judgements.Count(j => j.NeedsAttention);
}
