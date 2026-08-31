using System.Text;
using System.Text.RegularExpressions;
using NPOI.HSSF.UserModel;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Cli;

/// <summary>
/// 突合機能のテストデータ生成(テンプレート方式)。
///
/// 見本ファイル(実物のシフト表 .xls / 打刻データ .xlsx)を開き、値だけを差し替えて出力する。
/// 罫線・セル結合・列幅・行高・網掛け・数式・印刷設定は見本のものがそのまま残る。
///
/// 生成内容は判定コードの全パターンと境界値(29分/30分)を網羅し、乱数を使わないため
/// 何度生成しても同じ結果になる(回帰テスト向き)。
///
/// 対象月: 2026年7月(7/1=水曜)
///
/// 予定終了時刻は突合エンジンと同じ手順で決める(仕様書 13.2・14.4)。
///   正社員         … 予定開始 + 9時間30分 (judgement_rule.xml の fullTimeSpanMinutes)
///   パート・アルバイト … work_pattern.xml の終了時刻。未登録なら早退・時間外を判定しない
/// この2つがずれると通常勤務の日まで早退になるため、下記の定数はマスタと合わせて更新する。
/// </summary>
public static class TestDataGenerator
{
    private const int Year = 2026;
    private const int Month = 7;
    private const int Days = 31;

    /// <summary>正社員の拘束時間(分)。masters/judgement_rule.xml の fullTimeSpanMinutes と合わせる。</summary>
    private const int FullTimeSpanMinutes = 570;

    /// <summary>
    /// パート・アルバイトの予定終了時刻。masters/work_pattern.xml と合わせる。
    /// ここに無い開始時刻は「予定終了が未登録」となり、早退・時間外を判定しない。
    /// </summary>
    private static readonly Dictionary<int, int> PartTimeEnd = new()
    {
        [H(5)]      = H(14),     [H(5, 15)] = H(14, 15), [H(5, 30)] = H(14, 30),
        [H(6)]      = H(15),     [H(6, 30)] = H(15, 30),
        [H(7)]      = H(16),     [H(7, 30)] = H(16, 30),
        [H(8)]      = H(17),     [H(8, 30)] = H(17, 30),
        [H(9)]      = H(18),     [H(9, 30)] = H(18, 30),
        [H(10)]     = H(19),
    };

    /// <summary>1日分の予定と打刻。</summary>
    private sealed record DayPlan(string? Shift, string? Punch, bool ShiftIsTime);

    /// <summary>テスト社員。</summary>
    private sealed class Emp
    {
        public required int No { get; init; }
        /// <summary>打刻データ側の氏名(正式氏名)</summary>
        public required string PunchName { get; init; }
        /// <summary>シフト表側の氏名(表記ゆれ・役職表記のテスト用。null なら同じ)</summary>
        public string? ShiftName { get; init; }
        public required string Dept { get; init; }
        /// <summary>予定開始時刻(分)</summary>
        public required int Start { get; init; }
        /// <summary>雇用区分。早退の基準が変わるため、employee.xml の登録内容と合わせる。</summary>
        public EmploymentType Employment { get; init; } = EmploymentType.FullTime;
        /// <summary>公休の曜日</summary>
        public required DayOfWeek[] RestDays { get; init; }
        /// <summary>日別の上書き(シナリオの仕込み)</summary>
        public Dictionary<int, DayPlan> Overrides { get; } = new();
    }

    private static string T(int minutes) => $"{minutes / 60:00}:{minutes % 60:00}";
    private static int H(int h, int m = 0) => h * 60 + m;

    /// <summary>
    /// 突合エンジンが決める予定終了時刻(分)。null は「未登録のため早退・時間外を判定しない」。
    /// テストデータの打刻はこの時刻を基準に組み立てる。
    /// </summary>
    private static int? PlannedEnd(Emp e)
        => e.Employment == EmploymentType.FullTime
            ? e.Start + FullTimeSpanMinutes
            : PartTimeEnd.TryGetValue(e.Start, out var end) ? end : null;

    /// <summary>出勤=予定開始の <paramref name="inEarly"/> 分前、退勤=予定終了の <paramref name="outLate"/> 分後。</summary>
    private static string Punches(Emp e, int inEarly, int outLate)
        => T(e.Start - inEarly) + T((PlannedEnd(e) ?? e.Start + H(6)) + outLate);

    public static int Run(string outDir, string? shiftTemplate = null, string? punchTemplate = null)
    {
        shiftTemplate ??= Path.Combine("sample", "シフト表サンプル.xls");
        punchTemplate ??= Path.Combine("sample", "打刻データサンプル.xlsx");
        foreach (var t in new[] { shiftTemplate, punchTemplate })
            if (!File.Exists(t)) { Console.Error.WriteLine($"見本ファイルが見つかりません: {t}"); return 1; }

        Directory.CreateDirectory(outDir);

        // ================= テスト社員とシナリオの定義 =================
        // 部門は見本シート(7月修正)の枠に合わせる: 競技課=4枠 / 営業課=10枠(うち8枠を使用)
        var emps = new List<Emp>
        {
            // --- 競技課(4名) ---
            // 1) すべて正常 (7/31: 打刻が3件ある日=二度打ち)
            new() { No = 9001, PunchName = "佐藤 一郎", Dept = "競技課", Start = H(8),
                    RestDays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday } },
            // 2) 遅刻候補 (7/2: 1分遅刻の最小ケース, 7/16: 45分遅刻)
            new() { No = 9002, PunchName = "鈴木 二郎", Dept = "競技課", Start = H(8),
                    RestDays = new[] { DayOfWeek.Tuesday, DayOfWeek.Wednesday } },
            // 3) 早退候補 (7/3: 1分早退の最小ケース, 7/17: 90分早退)
            new() { No = 9003, PunchName = "高橋 三郎", Dept = "競技課", Start = H(8),
                    RestDays = new[] { DayOfWeek.Wednesday, DayOfWeek.Thursday } },
            // 4) 氏名の空白ゆれ(シフト=全角スペース、打刻=半角スペース)→ 自動解決されて正常
            //    (7/28: 打刻が4件ある日=中抜け)
            new() { No = 9004, PunchName = "田中 四郎", ShiftName = "田中　四郎", Dept = "競技課", Start = H(6),
                    RestDays = new[] { DayOfWeek.Thursday, DayOfWeek.Friday } },

            // --- 営業課(10名) ---
            // 5) 早出の境界値 (7/6: ちょうど30分早い→警告, 7/7: 29分→警告しない)
            new() { No = 9005, PunchName = "伊藤 五郎", Dept = "営業課", Start = H(9),
                    RestDays = new[] { DayOfWeek.Friday, DayOfWeek.Saturday } },
            // 6) 時間外の境界値 (7/8: ちょうど30分超過→警告, 7/9: 29分→警告しない)
            new() { No = 9006, PunchName = "渡辺 六郎", Dept = "営業課", Start = H(7),
                    RestDays = new[] { DayOfWeek.Saturday, DayOfWeek.Sunday } },
            // 7) 打刻漏れ系 (7/10: 両打刻なし, 7/14: 打刻1件のみ, 7/21: 打刻セルに時刻以外の文字)
            new() { No = 9007, PunchName = "山本 七子", Dept = "営業課", Start = H(6),
                    RestDays = new[] { DayOfWeek.Monday, DayOfWeek.Thursday } },
            // 8) 勤務区分に打刻 (7/4: 公休+打刻, 7/11: 有給+打刻, 7/18: 欠勤+打刻, 7/25: 有給・打刻なし=正常)
            new() { No = 9008, PunchName = "中村 八子", Dept = "営業課", Start = H(7, 30),
                    RestDays = new[] { DayOfWeek.Tuesday, DayOfWeek.Friday } },
            // 9) 早番(5:00)の通常勤務。シフト表は予定時刻が必ず入っている前提のため、
            //    「シフト空欄なのに打刻あり」の仕込みは置かない
            new() { No = 9009, PunchName = "小林 九子", Dept = "営業課", Start = H(5),
                    RestDays = new[] { DayOfWeek.Monday, DayOfWeek.Thursday } },
            // 10) 役職表記 → 氏名未解決 (別名マスタ登録で解消することを確認する)
            new() { No = 9010, PunchName = "加藤 十蔵", ShiftName = "加藤 主任", Dept = "営業課", Start = H(8),
                    RestDays = new[] { DayOfWeek.Wednesday, DayOfWeek.Saturday } },
            // 11) 出張・本部・未登録区分 (7/6: 出張, 7/20: 本部, 7/22: 未登録の「研修」)
            new() { No = 9011, PunchName = "山田 十一", Dept = "営業課", Start = H(6, 30),
                    RestDays = new[] { DayOfWeek.Tuesday, DayOfWeek.Thursday } },
            // 12) パート・予定終了が未登録(開始11:00は勤務パターンマスタにない)
            //     早退・時間外を判定しないため、ここで休憩時間の帯(6H/8H)の境界を確かめる
            new() { No = 9012, PunchName = "佐々木 十二", Dept = "営業課", Start = H(11),
                    Employment = EmploymentType.PartTime,
                    RestDays = new[] { DayOfWeek.Saturday, DayOfWeek.Sunday } },
            // 13) パート・予定終了が登録あり(9:00→18:00)
            //     同じ9:00始まりの正社員(伊藤 五郎)は18:30が基準になるので、その違いを確かめる
            new() { No = 9013, PunchName = "高木 十三", Dept = "営業課", Start = H(9),
                    Employment = EmploymentType.PartTime,
                    RestDays = new[] { DayOfWeek.Sunday, DayOfWeek.Monday } },
            // 14) アルバイト(7:00→16:00)。パート・アルバイト給与計算表の出力対象になる
            new() { No = 9014, PunchName = "大野 十四", Dept = "営業課", Start = H(7),
                    Employment = EmploymentType.Arbeit,
                    RestDays = new[] { DayOfWeek.Saturday, DayOfWeek.Sunday } },
        };

        Emp E(int no) => emps.First(e => e.No == no);

        void Ov(int no, int day, string? shift, string? punch)
            => E(no).Overrides[day] = new DayPlan(shift, punch, shift == null);

        // 打刻を「予定開始の n 分前 / 予定終了の n 分後」で組み立てる。
        // 予定終了はマスタから決まるため、しきい値を直書きせずに済む(マスタを変えてもずれない)。
        void OvAt(int no, int day, int inEarly, int outLate)
            => Ov(no, day, null, Punches(E(no), inEarly, outLate));

        OvAt(9002,  2,  -1, 10);   // 遅刻1分   (予定開始+1分に出勤)
        OvAt(9002, 16, -45, 10);   // 遅刻45分
        OvAt(9003,  3,   6, -1);   // 早退1分   (予定終了-1分に退勤)
        OvAt(9003, 17,   6, -90);  // 早退90分
        OvAt(9005,  6,  30, 10);   // 早出ちょうど30分 → 警告する側の境界
        OvAt(9005,  7,  29, 10);   // 早出29分         → 警告しない側の境界
        OvAt(9006,  8,   6, 30);   // 時間外ちょうど30分 → 警告する側の境界
        OvAt(9006,  9,   6, 29);   // 時間外29分         → 警告しない側の境界

        Ov(9007, 10, null, "");                           // 両打刻なし
        Ov(9007, 14, null, "05:55");                      // 打刻1件のみ
        Ov(9007, 21, null, "調整中");                     // 時刻を抽出できない文字列
        Ov(9008,  4, "公", "07:2416:10");                 // 公休に打刻
        Ov(9008, 11, "有", "07:2416:40");                 // 有給に打刻
        Ov(9008, 18, "欠", "07:2516:35");                 // 欠勤に打刻
        Ov(9008, 25, "有", "");                           // 有給(打刻なし) → 正常
        Ov(9011,  6, "出張", "");                          // 出張
        Ov(9011, 20, "本部", "");                          // 本部(出張扱い)
        Ov(9011, 22, "研修", "06:2416:10");                // 勤務区分マスタ未登録

        // パートの早退・時間外は「予定終了時刻」が基準(正社員の 予定開始+9時間30分 ではない)
        OvAt(9013,  2,   6, -1);   // 18:00 の1分前に退勤 → 早退(正社員基準なら 18:30 で31分早退)
        OvAt(9013,  3,   6, 30);   // 18:30 に退勤        → 時間外30分(正社員基準ならちょうど正常)
        OvAt(9014,  1,   6, -1);   // アルバイトも予定終了時刻が基準

        // 予定終了が未登録のパート(佐々木 十二)。早退・時間外を判定しないので、
        // ここで休憩時間の帯(仕様書 14.3)の境界を確かめる。打刻は15分丸め後の値で効く。
        Ov(9012,  1, null, "10:5417:04");                 // 丸め 11:00-17:00 = 6時間00分 → 休憩15分
        Ov(9012,  2, null, "10:5417:19");                 // 丸め 11:00-17:15 = 6時間15分 → 休憩45分
        Ov(9012,  3, null, "10:5419:04");                 // 丸め 11:00-19:00 = 8時間00分 → 休憩45分
        Ov(9012,  6, null, "10:5419:19");                 // 丸め 11:00-19:15 = 8時間15分 → 休憩90分

        // 1日に打刻が3件・4件あるケース。仕様書 第12章のとおり3件以上は要確認とし、
        // 時刻の判定は「最初=出勤 / 最後=退勤」で続ける(中間の打刻は無視)。
        Ov(9001, 31, null, "07:5407:5817:40");            // 3件: 二度打ち(時刻はすべて正常の範囲)
        Ov(9004, 28, null, "05:3005:5414:2015:40");       // 4件: 中抜け(先頭05:30で早出30分にもなる)

        // ================= 日別プランの展開 =================
        var plans = new Dictionary<(int No, int Day), DayPlan>();
        foreach (var e in emps)
        {
            for (int d = 1; d <= Days; d++)
            {
                if (e.Overrides.TryGetValue(d, out var ov))
                {
                    var shift = ov.Shift ?? T(e.Start);   // shift=null は「通常勤務のまま」
                    plans[(e.No, d)] = new DayPlan(shift, ov.Punch, ov.Shift == null);
                    continue;
                }

                var dow = new DateTime(Year, Month, d).DayOfWeek;
                if (e.RestDays.Contains(dow))
                {
                    plans[(e.No, d)] = new DayPlan("公", "", false);   // 公休・打刻なし
                }
                else
                {
                    // 通常勤務: 出勤は予定開始の6分前、退勤は予定終了の10分後 → 正常判定になる。
                    // 予定終了が未登録のパートは「開始+6時間」を仮の終わりにする(判定は行われない)。
                    plans[(e.No, d)] = new DayPlan(T(e.Start), Punches(e, 6, 10), true);
                }
            }
        }

        var shiftPath = Path.Combine(outDir, $"シフト表_テスト用{Year}年{Month:00}月.xls");
        WriteShiftFromTemplate(shiftTemplate, shiftPath, emps, plans);

        var punchPath = Path.Combine(outDir, $"打刻データ_テスト用{Year}年{Month:00}月.xlsx");
        WritePunchFromTemplate(punchTemplate, punchPath, emps, plans);

        var expectPath = Path.Combine(outDir, $"期待結果_テスト用{Year}年{Month:00}月.csv");
        WriteExpectations(expectPath);

        Console.WriteLine($"生成しました: {shiftPath}");
        Console.WriteLine($"生成しました: {punchPath}");
        Console.WriteLine($"生成しました: {expectPath}");
        Console.WriteLine();
        Console.WriteLine("検証コマンド例:");
        Console.WriteLine($"  run \"{shiftPath}\" \"7月テスト\" \"{punchPath}\" \"出席記録\"");
        return 0;
    }

    // ==================================================================
    //  シフト表: 見本の「7月修正」シートの枠(罫線・結合・列幅・数式)に値を差し替える
    // ==================================================================
    private static void WriteShiftFromTemplate(string templatePath, string outPath,
        List<Emp> emps, Dictionary<(int, int), DayPlan> plans)
    {
        HSSFWorkbook wb;
        using (var fs = new FileStream(templatePath, FileMode.Open, FileAccess.Read))
            wb = new HSSFWorkbook(fs);

        const string baseSheet = "7月修正";
        int keep = wb.GetSheetIndex(baseSheet);
        if (keep < 0) throw new InvalidOperationException($"見本に『{baseSheet}』シートがありません。");

        // 他の月のシート(実在氏名を含む)をテストデータに残さないよう削除する。
        // ※7月修正シートは他シートへの参照を持たないことを確認済み。
        for (int i = wb.NumberOfSheets - 1; i >= 0; i--)
            if (i != keep) wb.RemoveSheetAt(i);
        wb.SetSheetName(0, "7月テスト");
        wb.SetActiveSheet(0);
        var sheet = wb.GetSheetAt(0);

        // ---- 日番号行(G31:AK31 相当)を自動検出 ----
        var header = ExcelHelper.FindDayNumberRow(sheet, scanRows: 60, scanCols: 60, minRun: 10)
                     ?? throw new InvalidOperationException("見本シートの日番号行を検出できません。");
        int nameCol = 3;   // D列(見本の氏名列)

        // ---- 社員枠(氏名が入っている行)を洗い出す ----
        var slotRows = new List<int>();
        for (int r = header.RowIndex + 1; r <= sheet.LastRowNum; r++)
        {
            var t = ExcelHelper.Text(sheet.GetRow(r), nameCol);
            if (t.Length is < 2 or > 20) continue;
            if (t is "曜日" or "予約組数" or "行事") continue;
            slotRows.Add(r);
        }
        if (slotRows.Count < emps.Count)
            throw new InvalidOperationException($"見本の社員枠が {slotRows.Count} 行しかありません({emps.Count} 行必要)。");

        // ---- 列ごとの見本スタイルを控える(消去する前に取得) ----
        int c0 = header.StartColumn, c1 = header.StartColumn + Days - 1;
        var columnStyle = new ICellStyle?[c1 + 1];
        foreach (var r in slotRows)
            for (int c = c0; c <= c1; c++)
                columnStyle[c] ??= sheet.GetRow(r)?.GetCell(c)?.CellStyle;

        // 時刻セル用スタイルのキャッシュ(元スタイルを複製して h:mm 書式にする)
        var timeStyleCache = new Dictionary<short, ICellStyle>();
        ICellStyle TimeStyle(ICellStyle src)
        {
            if (timeStyleCache.TryGetValue(src.Index, out var cached)) return cached;
            var st = wb.CreateCellStyle();
            st.CloneStyleFrom(src);
            st.DataFormat = 20;   // 組込書式 "h:mm"
            timeStyleCache[src.Index] = st;
            return st;
        }

        // ---- 全社員枠の値を消去(氏名・日別値・AM列の備考) ----
        foreach (var r in slotRows)
        {
            var row = sheet.GetRow(r)!;
            row.GetCell(nameCol)?.SetBlank();
            for (int c = c0; c <= c1; c++) row.GetCell(c)?.SetBlank();
            row.GetCell(38)?.SetBlank();   // AM列: 「週2日 1日8時間…」などの備考
        }

        // ---- テスト社員を書き込む(枠の並び順どおり) ----
        for (int i = 0; i < emps.Count; i++)
        {
            var e = emps[i];
            var row = sheet.GetRow(slotRows[i])!;
            SetCell(row, nameCol, columnStyle[c0]).SetCellValue(e.ShiftName ?? e.PunchName);

            for (int d = 1; d <= Days; d++)
            {
                var plan = plans[(e.No, d)];
                if (string.IsNullOrEmpty(plan.Shift)) continue;   // シフトなし(空欄)

                int c = c0 + d - 1;
                var cell = SetCell(row, c, columnStyle[c]);
                if (plan.ShiftIsTime)
                {
                    var p = plan.Shift.Split(':');
                    cell.SetCellValue((int.Parse(p[0]) * 60 + int.Parse(p[1])) / 1440.0);
                    cell.CellStyle = TimeStyle(cell.CellStyle);
                }
                else
                {
                    cell.SetCellValue(plan.Shift);   // 公・有・欠・出張・本部・特(書式は枠のまま)
                }
            }
        }

        // ---- 対象期間の表記を正す ----
        ReplacePeriodText(sheet, header.RowIndex, $"{Year}/{Month}/1～{Year}/{Month}/{Days}",
            new Regex(@"\d{4}\s*/\s*\d{1,2}\s*/\s*\d{1,2}.*[～~]"));

        wb.ForceFormulaRecalculation = true;   // 休日数などの既存数式をExcelで再計算させる
        using (var os = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            wb.Write(os, leaveOpen: false);
        wb.Close();
    }

    // ==================================================================
    //  打刻データ: 見本の「出席記録」シートの枠に値を差し替え、31日分へ拡張する
    // ==================================================================
    private static void WritePunchFromTemplate(string templatePath, string outPath,
        List<Emp> emps, Dictionary<(int, int), DayPlan> plans)
    {
        XSSFWorkbook wb;
        using (var fs = new FileStream(templatePath, FileMode.Open, FileAccess.Read))
            wb = new XSSFWorkbook(fs);

        var sheet = wb.GetSheet("出席記録") ?? wb.GetSheetAt(0);
        var header = ExcelHelper.FindDayNumberRow(sheet, scanRows: 20, scanCols: 40, minRun: 5)
                     ?? throw new InvalidOperationException("見本の日番号行を検出できません。");
        int c0 = header.StartColumn;

        // ---- 社員ブロック(『作業番号:』の行)を洗い出す ----
        var metaRows = new List<int>();
        for (int r = header.RowIndex + 1; r <= sheet.LastRowNum; r++)
            if (ExcelHelper.Text(sheet.GetRow(r), 0).Contains("作業番号")) metaRows.Add(r);
        if (metaRows.Count < emps.Count)
            throw new InvalidOperationException($"見本の社員ブロックが {metaRows.Count} 件しかありません。");

        // ---- 列ごとの見本スタイルを控える(日番号行・打刻行) ----
        var dayRowObj = sheet.GetRow(header.RowIndex)!;
        var dayStyle = dayRowObj.GetCell(c0)?.CellStyle;
        var punchColStyle = new ICellStyle?[c0 + Days];
        foreach (var m in metaRows)
        {
            var prow = sheet.GetRow(m + 1);
            if (prow == null) continue;
            for (int c = c0; c < c0 + Days; c++)
                punchColStyle[c] ??= prow.GetCell(c)?.CellStyle;
        }

        // ---- 日番号を 1..31 に拡張(見本は週次出力で 1..7 のみ) ----
        for (int d = 1; d <= Days; d++)
            SetCell(dayRowObj, c0 + d - 1, dayStyle).SetCellValue((double)d);

        // ---- 見出しの期間表記を月次に書き換え、週次出力の名残(右側の見出し)を消す ----
        for (int r = 0; r < header.RowIndex; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;
            for (int c = 0; c < Math.Max((int)row.LastCellNum, 1); c++)
            {
                var cell = row.GetCell(c);
                var text = ExcelHelper.Text(cell);
                if (text.Length == 0) continue;
                if (Regex.IsMatch(text, @"\d{4}-\d{2}-\d{2}\s*~"))
                    cell!.SetCellValue($"{Year:0000}-{Month:00}-01 ~ {Year:0000}-{Month:00}-{Days:00}");
                else if (c > 4 && (text == "勤怠時間" || Regex.IsMatch(text, @"^\d{4}-\d{2}-\d{2}$")))
                    cell!.SetBlank();   // 2ブロック目の「勤怠時間 2026-07-08」
            }
        }

        // ---- 先頭12ブロックへテスト社員を書き込む ----
        for (int i = 0; i < emps.Count; i++)
        {
            var e = emps[i];
            var meta = sheet.GetRow(metaRows[i])!;
            // 作業番号は文字列で書く(見本の列幅が2桁向けで、数値だと「###」表示になるため)
            SetValueKeepStyle(meta, 2, cell => cell.SetCellValue(e.No.ToString()));
            SetValueKeepStyle(meta, 10, cell => cell.SetCellValue(e.PunchName));       // 名前
            SetValueKeepStyle(meta, 20, cell => cell.SetCellValue(e.Dept));            // 部門

            var prow = sheet.GetRow(metaRows[i] + 1) ?? sheet.CreateRow(metaRows[i] + 1);
            for (int c = c0; c < c0 + Days; c++) prow.GetCell(c)?.SetBlank();          // 旧値を消去
            for (int d = 1; d <= Days; d++)
            {
                var punch = plans[(e.No, d)].Punch;
                if (string.IsNullOrEmpty(punch)) continue;
                SetCell(prow, c0 + d - 1, punchColStyle[c0 + d - 1]).SetCellValue(punch);
            }
        }

        // ---- 使わない残りのブロックは行ごと削除する ----
        int deleteFrom = metaRows[emps.Count];
        for (int r = sheet.LastRowNum; r >= deleteFrom; r--)
        {
            var row = sheet.GetRow(r);
            if (row != null) sheet.RemoveRow(row);
        }

        using (var os = new FileStream(outPath, FileMode.Create, FileAccess.Write))
            wb.Write(os, leaveOpen: false);
        wb.Close();
    }

    // ==================================================================
    private static void WriteExpectations(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("日付,氏名,シナリオ,期待される判定,補足");
        void E(int day, string name, string scenario, string expected, string note = "")
            => sb.AppendLine($"{(day == 0 ? "全勤務日" : $"{Year}/{Month:00}/{day:00}")},{name},{scenario},{expected},{note}");

        E( 0, "佐藤 一郎",   "すべて正常な社員",              "正常", "出勤=予定開始の6分前 / 退勤=予定終了の10分後。公休日は公休(正常)");
        E(31, "佐藤 一郎",   "1日に打刻が3件(二度打ち)",      "要確認(複数打刻)", "出勤=最初の07:54 / 退勤=最後の17:40。中間の07:58は無視される");
        E( 2, "鈴木 二郎",   "1分の遅刻",                     "遅刻候補(要確認)", "許容0分のため1分でも検知");
        E(16, "鈴木 二郎",   "45分の遅刻",                    "遅刻候補(要確認)");
        E( 3, "高橋 三郎",   "1分の早退",                     "早退候補(要確認)", "正社員の基準 予定開始+9時間30分 = 17:30 の1分前");
        E(17, "高橋 三郎",   "90分の早退",                    "早退候補(要確認)");
        E( 0, "田中 四郎",   "氏名の空白ゆれ(全角/半角)",     "正常", "マスタ登録なしで自動解決されること");
        E(28, "田中 四郎",   "1日に打刻が4件(中抜け)",        "要確認(複数打刻)", "出勤=最初の05:30(早出30分にもなる) / 退勤=最後の15:40");
        E( 6, "伊藤 五郎",   "ちょうど30分の早出",             "早出30分以上(警告)", "30分=警告する側の境界");
        E( 7, "伊藤 五郎",   "29分の早出",                    "正常", "29分=警告しない側の境界");
        E( 8, "渡辺 六郎",   "ちょうど30分の時間外",           "時間外30分以上(警告)", "30分=警告する側の境界");
        E( 9, "渡辺 六郎",   "29分の時間外",                  "正常", "29分=警告しない側の境界");
        E(10, "山本 七子",   "勤務予定なのに打刻なし",         "両打刻なし(要確認)");
        E(14, "山本 七子",   "打刻が1件だけ",                 "打刻漏れ(要確認)", "1件のときに出勤・退勤の区別はしない");
        E(21, "山本 七子",   "打刻セルに『調整中』の文字",     "両打刻なし(要確認)", "処理ログに時刻抽出不能の警告も出る");
        E( 4, "中村 八子",   "公休の日に打刻",                "公休に打刻あり(要確認)");
        E(11, "中村 八子",   "有給の日に打刻",                "有給に打刻あり(要確認)");
        E(18, "中村 八子",   "欠勤の日に打刻",                "その他に打刻あり(要確認)", "『欠』は勤務区分マスタで『その他』(要確認 Q-04)");
        E(25, "中村 八子",   "有給(打刻なし)",                "有給(正常)");
        E( 0, "加藤 主任",   "シフト側が役職表記",            "氏名未解決(エラー)", "name_alias.xml に <alias source=\"加藤 主任\" canonical=\"加藤 十蔵\"/> を登録すると解消");
        E( 0, "加藤 十蔵",   "打刻側(上と同一人物)",          "シフトなし打刻(要確認)", "別名登録で正常に突合される");
        E( 6, "山田 十一",   "出張(打刻なし)",                "終日出張(正常)", "申請書の確認メッセージが出る");
        E(20, "山田 十一",   "本部(打刻なし)",                "終日出張(正常)", "『本部』は勤務区分マスタで出張扱い");
        E(22, "山田 十一",   "未登録の勤務区分『研修』",      "その他に打刻あり(要確認)", "マスタ未登録の文字値は『その他』として扱う");

        // --- パート・アルバイト(employee.xml の 9000番台に登録) ---
        E( 0, "佐々木 十二", "パート・予定終了が未登録",      "正常", "開始11:00は work_pattern.xml に無い。早退・時間外は判定しない");
        E( 1, "佐々木 十二", "休憩の境界 6時間ちょうど",       "休憩15分", "丸め 11:00-17:00。パート給与計算表で確認する");
        E( 2, "佐々木 十二", "休憩の境界 6時間15分",           "休憩45分", "丸め 11:00-17:15");
        E( 3, "佐々木 十二", "休憩の境界 8時間ちょうど",       "休憩45分", "丸め 11:00-19:00");
        E( 6, "佐々木 十二", "休憩の境界 8時間15分",           "休憩90分", "丸め 11:00-19:15");
        E( 0, "高木 十三",   "パート・予定終了が登録あり",    "正常", "9:00→18:00。同じ9:00始まりの正社員(伊藤 五郎)は18:30が基準");
        E( 2, "高木 十三",   "予定終了(18:00)の1分前に退勤",  "早退候補(要確認)", "正社員の基準(18:30)なら31分の早退になる日");
        E( 3, "高木 十三",   "18:30 に退勤",                  "時間外30分以上(警告)", "正社員の基準(18:30)ならちょうど正常になる日");
        E( 1, "大野 十四",   "アルバイト・1分の早退",         "早退候補(要確認)", "7:00→16:00。パート・アルバイト給与計算表の出力対象");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));   // Excelで開けるようBOM付き
    }

    // ------------------------------------------------------------------
    /// <summary>セルを取得(無ければ作成)し、新規作成時のみ見本スタイルを適用する。</summary>
    private static ICell SetCell(IRow row, int col, ICellStyle? templateStyle)
    {
        var cell = row.GetCell(col);
        if (cell == null)
        {
            cell = row.CreateCell(col);
            if (templateStyle != null) cell.CellStyle = templateStyle;
        }
        return cell;
    }

    /// <summary>既存セルの書式を保ったまま値だけ差し替える。</summary>
    private static void SetValueKeepStyle(IRow row, int col, Action<ICell> setter)
    {
        var cell = row.GetCell(col) ?? row.CreateCell(col);
        setter(cell);
    }

    /// <summary>日番号行より上にある期間表記を探して書き換える。</summary>
    private static void ReplacePeriodText(ISheet sheet, int dayHeaderRow, string newText, Regex pattern)
    {
        for (int r = Math.Max(0, dayHeaderRow - 10); r <= dayHeaderRow; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;
            for (int c = 0; c < 12; c++)
            {
                var cell = row.GetCell(c);
                var text = ExcelHelper.Text(cell);
                if (text.Length > 0 && pattern.IsMatch(text)) { cell!.SetCellValue(newText); return; }
            }
        }
    }
}
