using System.Text;
using System.Xml.Linq;
using NPOI.SS.UserModel;
using TakaneAttendance.Core.Excel;
using TakaneAttendance.Core.Masters;
using TakaneAttendance.Core.Models;
using TakaneAttendance.Core.Naming;

namespace TakaneAttendance.Cli;

/// <summary>
/// お客様の「従業員データ.xlsx」から従業員マスタ(employee.xml)を作る。
///
/// 従業員マスタは氏名と所属部を必須とする。所属部は所定労働時間マスタの引き当てキーで、
/// 年間カレンダーの【部門別所定労働時間】(業務部・総務部・食堂部・コース管理部)と同じ粒度になる。
///
/// 従業員データには同じ様式のシートが2枚あり、書かれている「所属」の粒度が違う。
///   「従業員データ」            … 部の粒度(業務部 業務Ⅰ課 / 総務部 / コース管理部)
///   「従業員データ (定期健康診断用)」… 課の粒度(営業課 / 競技課 / 営繕課 / ホール課)
/// 在籍者もシートによって異なる(新しい方にしかいない方が30名ほどいる)ため、
/// 両方のシートを読んで社員番号で突き合わせ、
///   ・所属部 … 部の粒度で書かれている方を採用する
///   ・氏名・部門・入社年月日 … 後ろのシート(新しい方)を採用する
/// とする。部の粒度がどこにも無い方は、両シートに載っている方の対応から
/// 「課 → 部」の表を作って補い、それでも決まらない場合は書かれている所属をそのまま入れて警告する。
///
/// 雇用区分の列は無いため、パート・アルバイトは
/// 「[提出]パートアルバイト給与計算表.xlsx」のシート構成(1人1シート)から特定する。
/// </summary>
internal static class EmployeeImporter
{
    /// <summary>1人1シートではない見出し・雛形のシート。</summary>
    private static readonly string[] SkipSheets = { "原本", "原本（入力例）", "原本(入力例)" };

    /// <summary>従業員データの1行(シートごとの生の値)。</summary>
    private sealed record RawRow(int SheetOrder, string SheetName, string No, string Name,
                                 string Affiliation, string Workplace, DateOnly? Joined);

    public static int Run(string employeeBookPath, string? payrollBookPath, string outputPath, string? sheetName)
    {
        // 別名マスタを通して氏名を解決する。給与計算表と従業員データで
        // 異体字(濵島/濱島)が分かれている実例があるため。
        var aliasPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? ".", MasterSet.AliasFileName);
        var alias = AliasMaster.Load(File.Exists(aliasPath) ? aliasPath : null);
        foreach (var m in alias.Messages) Console.WriteLine("  " + m);

        var partTimeNames = payrollBookPath == null
            ? new Dictionary<string, string>()
            : ReadPartTimeNames(payrollBookPath, alias);

        if (payrollBookPath != null)
        {
            Console.WriteLine($"パート・アルバイト : {partTimeNames.Count} 名 ({Path.GetFileName(payrollBookPath)} のシートから特定)");
            foreach (var name in partTimeNames.Values.Order()) Console.WriteLine($"    {name}");
            Console.WriteLine();
        }

        var entries = ReadEmployees(employeeBookPath, sheetName, partTimeNames, out var usedSheets, out var messages);
        foreach (var m in messages) Console.WriteLine("  " + m);

        // 従業員データに無い項目(1日の拘束時間・基本時給)は、今あるマスタから引き継ぐ
        entries = CarryOverManualValues(entries, outputPath);

        Console.WriteLine($"従業員データ : {Path.GetFileName(employeeBookPath)} [{string.Join(" + ", usedSheets)}] から {entries.Count} 名");

        // 給与計算表にいるのに従業員データで見つからなかった方(氏名の表記ゆれ)を知らせる
        var registered = entries.Select(e => NameNormalizer.Normalize(e.CanonicalName)).ToHashSet();
        var missing = partTimeNames.Where(p => !registered.Contains(p.Key)).Select(p => p.Value).ToList();
        if (missing.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  [注意] 給与計算表にあるが従業員データで見つからない氏名(異体字などの表記ゆれ):");
            Console.WriteLine("         name_alias.xml に下記の行を足すと解決します(canonical に従業員データ側の氏名)。");
            foreach (var name in missing)
                Console.WriteLine("    <alias source=\"" + name + "\" canonical=\"\" note=\"給与計算表の表記\"/>");
        }

        // 雇用区分を判断できなかった部門(キャディ・売店・登録プロなど)を知らせる
        var unclear = entries
            .Where(e => e.Employment == EmploymentType.FullTime)
            .Where(e => e.ShiftPattern.Contains("キャディ") || e.ShiftPattern.Contains("売店") || e.ShiftPattern.Contains("プロ"))
            .GroupBy(e => e.ShiftPattern)
            .OrderByDescending(g => g.Count())
            .ToList();

        if (unclear.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  [要確認] 雇用区分を判断できず、正社員として登録した部門:");
            foreach (var g in unclear) Console.WriteLine($"    {g.Key,-20} {g.Count(),3} 名");
        }

        WriteXml(outputPath, entries, employeeBookPath, payrollBookPath, usedSheets);

        Console.WriteLine();
        Console.WriteLine("  所属部ごとの人数(所定労働時間マスタの引き当てキー):");
        foreach (var g in entries.GroupBy(e => e.Division).OrderByDescending(g => g.Count()))
            Console.WriteLine($"    {(g.Key.Length > 0 ? g.Key : "(未設定)"),-16} {g.Count(),3} 名");

        Console.WriteLine();
        Console.WriteLine($"出力 : {outputPath}");
        Console.WriteLine($"  正社員     : {entries.Count(e => e.Employment == EmploymentType.FullTime),4} 名");
        Console.WriteLine($"  パート     : {entries.Count(e => e.Employment == EmploymentType.PartTime),4} 名");
        Console.WriteLine($"  アルバイト : {entries.Count(e => e.Employment == EmploymentType.Arbeit),4} 名");
        return 0;
    }

    /// <summary>
    /// 1日の拘束時間・基本時給・管理区分を、今ある従業員マスタから引き継ぐ。
    ///
    /// この3つは従業員データ.xlsx に無く、画面(マスタの編集)で入れていただく値のため、
    /// 作り直しで消えないように氏名で突き合わせて持ち越す。
    /// </summary>
    private static List<EmployeeEntry> CarryOverManualValues(List<EmployeeEntry> entries, string outputPath)
    {
        if (!File.Exists(outputPath)) return entries;

        var previous = new Dictionary<string, EmployeeEntry>();
        foreach (var e in EmployeeMaster.Load(outputPath).All)
            previous[NameNormalizer.Normalize(e.CanonicalName)] = e;

        int carried = 0;
        var result = new List<EmployeeEntry>(entries.Count);
        foreach (var e in entries)
        {
            if (!previous.TryGetValue(NameNormalizer.Normalize(e.CanonicalName), out var old) ||
                (old.WorkHours == null && old.HourlyWage == null && old.IsManaged))
            {
                result.Add(e);
                continue;
            }

            carried++;
            result.Add(new EmployeeEntry
            {
                CanonicalName = e.CanonicalName,
                EmployeeNo = e.EmployeeNo,
                Division = e.Division,
                Department = e.Department,
                Employment = e.Employment,
                ShiftPattern = e.ShiftPattern,
                WorkHours = old.WorkHours,
                HourlyWage = old.HourlyWage,
                JoinedOn = e.JoinedOn,
                LeftOn = e.LeftOn,
                IsManaged = old.IsManaged
            });
        }

        if (carried > 0)
            Console.WriteLine($"  1日の拘束時間・基本時給・管理区分は、今のマスタから {carried} 名分を引き継ぎました。");
        return result;
    }

    /// <summary>給与計算表の各シートから、パート・アルバイトの正式氏名を集める。</summary>
    private static Dictionary<string, string> ReadPartTimeNames(string path, AliasMaster alias)
    {
        var found = new Dictionary<string, string>();
        using var wb = ExcelHelper.OpenWorkbook(path);

        for (int i = 0; i < wb.NumberOfSheets; i++)
        {
            var sheet = wb.GetSheetAt(i);
            if (sheet == null || SkipSheets.Contains(sheet.SheetName)) continue;
            // 部門の見出しだけのシート(中身が無い)は飛ばす
            if (sheet.LastRowNum <= 0) continue;

            var name = FindNameBesideLabel(sheet);
            if (name == null) continue;

            // 別名マスタで正式氏名に寄せてから登録する(従業員データ側の表記と突き合わせるため)
            var normalized = NameNormalizer.Normalize(name);
            var canonical = alias.Resolve(normalized);
            found[canonical != null ? NameNormalizer.Normalize(canonical) : normalized] = name;
        }
        return found;
    }

    /// <summary>「氏名」と書かれたセルの右隣から正式氏名を取る。</summary>
    private static string? FindNameBesideLabel(ISheet sheet)
    {
        for (int r = 0; r <= Math.Min(sheet.LastRowNum, 6); r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            for (int c = 0; c < Math.Min((int)row.LastCellNum, 6); c++)
            {
                if (ExcelHelper.Text(row.GetCell(c)) != "氏名") continue;

                // 結合セルのことがあるため、右へ数セル探す
                for (int k = c + 1; k < Math.Min((int)row.LastCellNum, c + 4); k++)
                {
                    var value = ExcelHelper.Text(row.GetCell(k));
                    if (value.Length > 0) return value;
                }
            }
        }
        return null;
    }

    private static List<EmployeeEntry> ReadEmployees(
        string path, string? sheetName, IReadOnlyDictionary<string, string> partTimeNames,
        out List<string> usedSheets, out List<string> messages)
    {
        messages = new List<string>();
        usedSheets = new List<string>();

        using var wb = ExcelHelper.OpenWorkbook(path);

        // 同じ様式のシートをすべて読む(所属の粒度と在籍者がシートごとに違うため)
        var rows = new List<RawRow>();
        for (int i = 0; i < wb.NumberOfSheets; i++)
        {
            var sheet = wb.GetSheetAt(i);
            if (sheet == null) continue;
            if (sheetName != null && sheet.SheetName != sheetName) continue;

            var read = ReadSheet(sheet, i, messages);
            if (read.Count == 0) continue;

            usedSheets.Add(sheet.SheetName);
            rows.AddRange(read);
        }

        if (usedSheets.Count == 0)
            throw new InvalidOperationException(sheetName != null
                ? $"シート '{sheetName}' から社員行を読めません。"
                : "見出し行(社員番号・氏名)を持つシートがありません。");

        var bySection = BuildSectionToDivision(rows, messages);

        // 社員番号で1人にまとめる
        var entries = new List<EmployeeEntry>();
        var unresolved = new List<string>();
        foreach (var group in rows.GroupBy(r => r.No).OrderBy(g => g.Key.PadLeft(6, '0')))
        {
            var ordered = group.OrderBy(r => r.SheetOrder).ToList();
            var newest = ordered[^1];

            // 所属部は「部」の粒度で書かれているシートを優先する
            var withDivision = ordered.FirstOrDefault(r => IsDivisionForm(HeadOf(r.Affiliation)));
            string division, section;

            if (withDivision != null)
            {
                division = HeadOf(withDivision.Affiliation);
                section = TailOf(withDivision.Affiliation);
                if (section.Length == 0)
                    section = ordered.Select(r => r.Affiliation)
                                     .FirstOrDefault(a => a.Length > 0 && a != division) ?? "";
            }
            else
            {
                // どのシートも課の粒度。両シートに載っている方の対応から補う
                var source = ordered.Select(r => r.Affiliation).FirstOrDefault(a => a.Length > 0) ?? "";
                section = source;
                if (bySection.TryGetValue(source, out var mapped)) division = mapped;
                else
                {
                    division = source;
                    if (source.Length > 0) unresolved.Add($"{newest.Name}({source})");
                }
            }

            if (newest.Name.Length == 0) continue;

            var key = NameNormalizer.Normalize(newest.Name);
            if (division.Length == 0)
                messages.Add($"[注意] 「{newest.Name}」の所属部が空です。所定労働時間マスタを引き当てられません。");

            entries.Add(new EmployeeEntry
            {
                CanonicalName = newest.Name,
                EmployeeNo = newest.No,
                Division = division,
                Department = section == division ? "" : section,
                Employment = ResolveEmployment(key, newest.Workplace, partTimeNames),
                // 部門(ハウス・コース・パートキャディ など)は雇用区分の手掛かりでもある
                ShiftPattern = newest.Workplace,
                JoinedOn = ordered.Select(r => r.Joined).LastOrDefault(j => j != null)
            });
        }

        // 氏名の重複(社員番号違い)は突合キーが同じになるため、先に読んだ方を残す
        var seen = new HashSet<string>();
        var unique = new List<EmployeeEntry>();
        foreach (var e in entries)
        {
            if (seen.Add(NameNormalizer.Normalize(e.CanonicalName))) unique.Add(e);
            else messages.Add($"[注意] 氏名が重複しています(先に読んだ行を使います): {e.CanonicalName} (社員番号 {e.EmployeeNo})");
        }

        if (unresolved.Count > 0)
            messages.Add($"[要確認] 所属部を部の粒度に寄せられなかった方 {unresolved.Count} 名: " +
                         string.Join(" , ", unresolved.Take(10)) + (unresolved.Count > 10 ? " ..." : "") +
                         " … 書かれている所属をそのまま所属部にしました。");

        return unique;
    }

    /// <summary>1シート分の社員行を読む。見出しが無いシートは空を返す。</summary>
    private static List<RawRow> ReadSheet(ISheet sheet, int order, List<string> messages)
    {
        var rows = new List<RawRow>();

        var header = FindHeader(sheet, out int headerRow);
        if (header.Count == 0) return rows;

        int colNo = Column(header, "社員番号");
        int colName = Column(header, "氏名");
        int colAffiliation = Column(header, "所属");
        int colWorkplace = Column(header, "部門");
        int colJoin = Column(header, "入社年月日");

        for (int r = headerRow + 1; r <= sheet.LastRowNum; r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            var name = ExcelHelper.Text(row.GetCell(colName));
            var no = colNo >= 0 ? ExcelHelper.Text(row.GetCell(colNo)) : "";

            // 集計行(社員番号が無く、氏名欄に人数が入る)や未入力行("0")を除く
            if (name.Length == 0 || name == "0" || no.Length == 0) continue;

            rows.Add(new RawRow(
                order, sheet.SheetName, no, name,
                colAffiliation >= 0 ? ExcelHelper.Text(row.GetCell(colAffiliation)) : "",
                colWorkplace >= 0 ? ExcelHelper.Text(row.GetCell(colWorkplace)) : "",
                colJoin >= 0 ? ReadDate(row.GetCell(colJoin)) : null));
        }
        return rows;
    }

    /// <summary>
    /// 「課 → 所属部」の対応表を、複数シートに載っている方から作る。
    /// 例) シート1で「営業課」の方がシート0では「業務部 業務Ⅰ課」→ 営業課 = 業務部。
    /// 同じ課に複数の部が現れた場合は、人数の多い方を採る。
    /// </summary>
    private static Dictionary<string, string> BuildSectionToDivision(List<RawRow> rows, List<string> messages)
    {
        // 同じ方に付いている所属どうしを線でつなぐ
        //   例) 「業務部 業務Ⅰ課」と「営業課」が同じ方 → 営業課 は 業務部
        //       「食堂課」と「ホール課」が同じ方、「食堂課」と「食堂部」が別の方
        //       → ホール課 は 食堂課 を経由して 食堂部
        var links = new Dictionary<string, Dictionary<string, int>>();
        void Link(string a, string b)
        {
            if (a.Length == 0 || b.Length == 0 || a == b) return;
            if (!links.TryGetValue(a, out var c)) links[a] = c = new Dictionary<string, int>();
            c[b] = c.GetValueOrDefault(b) + 1;
        }

        foreach (var group in rows.GroupBy(r => r.No))
        {
            var affiliations = group.Select(r => r.Affiliation.Trim()).Where(a => a.Length > 0).Distinct().ToList();
            foreach (var a in affiliations)
                foreach (var b in affiliations)
                    Link(a, b);
        }

        // 部の粒度にたどり着くまで、近い方からたどる
        var map = new Dictionary<string, string>();
        foreach (var start in links.Keys)
        {
            if (IsDivisionForm(HeadOf(start))) continue;

            var visited = new HashSet<string> { start };
            var queue = new Queue<string>();
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                if (!links.TryGetValue(current, out var next)) continue;

                // 同じ距離なら、つながっている人数の多い方を優先する
                foreach (var (candidate, _) in next.OrderByDescending(x => x.Value))
                {
                    if (!visited.Add(candidate)) continue;
                    if (IsDivisionForm(HeadOf(candidate))) { map[start] = HeadOf(candidate); break; }
                    queue.Enqueue(candidate);
                }
                if (map.ContainsKey(start)) break;
            }
        }

        if (map.Count > 0)
            messages.Add("[所属部の補完] " + string.Join(" , ", map.OrderBy(m => m.Key).Select(m => $"{m.Key}→{m.Value}")));
        return map;
    }

    /// <summary>「業務部 業務Ⅰ課」の先頭語。</summary>
    private static string HeadOf(string affiliation)
    {
        var s = affiliation.Trim();
        if (s.Length == 0) return "";
        int i = s.IndexOfAny(new[] { ' ', '　' });
        return i < 0 ? s : s[..i];
    }

    /// <summary>「業務部 業務Ⅰ課」の2語目以降。1語なら空。</summary>
    private static string TailOf(string affiliation)
    {
        var s = affiliation.Trim();
        int i = s.IndexOfAny(new[] { ' ', '　' });
        return i < 0 ? "" : s[(i + 1)..].Trim();
    }

    /// <summary>「業務部」「管理部門」のような部の粒度か。「営業課」「食堂課」は課の粒度。</summary>
    private static bool IsDivisionForm(string name)
        => name.Length > 0 && (name.EndsWith("部") || name.EndsWith("部門"));

    /// <summary>
    /// 雇用区分を決める。
    ///   1. パート・アルバイト給与計算表にシートがある → パート
    ///   2. 部門の名称に「アルバイト」「パート」が入っている → その区分
    ///   3. それ以外は正社員
    /// 「ハウスキャディ」「準キャディ」「登録プロ」「売店」は判断できないため正社員として扱い、
    /// 呼び出し元で要確認として一覧に出す。
    /// </summary>
    private static EmploymentType ResolveEmployment(
        string normalizedKey, string workplace, IReadOnlyDictionary<string, string> partTimeNames)
    {
        if (partTimeNames.ContainsKey(normalizedKey)) return EmploymentType.PartTime;
        if (workplace.Contains("アルバイト")) return EmploymentType.Arbeit;
        if (workplace.Contains("パート")) return EmploymentType.PartTime;
        return EmploymentType.FullTime;
    }

    private static Dictionary<string, int> FindHeader(ISheet sheet, out int headerRow)
    {
        for (int r = 0; r <= Math.Min(sheet.LastRowNum, 10); r++)
        {
            var row = sheet.GetRow(r);
            if (row == null) continue;

            var map = new Dictionary<string, int>();
            for (int c = 0; c < row.LastCellNum; c++)
            {
                var text = ExcelHelper.Text(row.GetCell(c));
                if (text.Length > 0) map.TryAdd(text, c);
            }

            if (map.ContainsKey("氏名") && map.ContainsKey("社員番号"))
            {
                headerRow = r;
                return map;
            }
        }
        headerRow = -1;
        return new Dictionary<string, int>();
    }

    private static int Column(IReadOnlyDictionary<string, int> header, string name)
        => header.TryGetValue(name, out var c) ? c : -1;

    /// <summary>Excel のシリアル値・日付セル・文字列のいずれでも日付として読む。</summary>
    private static DateOnly? ReadDate(ICell? cell)
    {
        if (cell == null) return null;
        try
        {
            if (cell.CellType == CellType.Numeric)
                return DateOnly.FromDateTime(DateUtil.GetJavaDate(cell.NumericCellValue));
        }
        catch (Exception)
        {
            // シリアル値として読めない場合は文字列として試す
        }
        return DateOnly.TryParse(ExcelHelper.Text(cell), out var d) ? d : null;
    }

    private static void WriteXml(
        string path, List<EmployeeEntry> entries,
        string employeeBookPath, string? payrollBookPath, List<string> usedSheets)
    {
        var comment = new StringBuilder()
            .AppendLine()
            .AppendLine("  従業員マスタ (統合仕様書 v3.0 第10.4章・第17章)")
            .AppendLine()
            .AppendLine("  このファイルは import-employees コマンドで自動生成しています。手で編集した内容は")
            .AppendLine("  再生成で失われます。恒久的な修正は元データ側で行ってください。")
            .AppendLine()
            .AppendLine($"    従業員データ : {Path.GetFileName(employeeBookPath)} [{string.Join(" + ", usedSheets)}]")
            .AppendLine($"    雇用区分     : {(payrollBookPath == null ? "(判定なし。全員を正社員として登録)" : Path.GetFileName(payrollBookPath) + " のシート構成から判定")}")
            .AppendLine()
            .AppendLine("  書式: <employee no=\"社員番号\" name=\"氏名\" division=\"所属部\" department=\"所属課\"")
            .AppendLine("                 employment=\"雇用区分\" pattern=\"部門(勤務地)\"")
            .AppendLine("                 workHours=\"9:00\" hourlyWage=\"1200\" joined=\"入社日\" managed=\"true\"/>")
            .AppendLine()
            .AppendLine("    workHours(1日の拘束時間)・hourlyWage(基本時給)・managed(管理区分) は従業員データに")
            .AppendLine("    無いため、画面(マスタの編集 → 従業員)で入れていただく値です。作り直しても引き継ぎます。")
            .AppendLine()
            .AppendLine("    managed=\"false\" の方は勤怠管理の対象外です。シフト表・打刻データに載っていても、")
            .AppendLine("    突合結果の一覧にも帳票にも出しません。省略した場合は対象(true)になります。")
            .AppendLine()
            .AppendLine("    name(氏名) と division(所属部) は必須です。")
            .AppendLine("    division は所定労働時間マスタ(working_hours.xml)の引き当てキーで、")
            .AppendLine("    年間カレンダーの【部門別所定労働時間】と同じ粒度(業務部・総務部・食堂部・")
            .AppendLine("    コース管理部)になります。同じ文字を working_hours.xml 側にも書いてください。")
            .AppendLine()
            .AppendLine("  【要確認】所属部の粒度")
            .AppendLine("    従業員データはシートによって所属の粒度が違います(部の粒度と課の粒度)。")
            .AppendLine("    両方のシートを読み、部の粒度で書かれている方を所属部として採用しています。")
            .AppendLine("    どのシートも課の粒度だった方は、両シートに載っている方の対応から補完しました。")
            .AppendLine()
            .AppendLine("  【要確認】雇用区分の判定方法")
            .AppendLine("    従業員データに雇用区分の列が無いため、パート・アルバイト給与計算表に")
            .AppendLine("    シートがある方をパートとして登録しています。")
            .AppendLine("    ・パートとアルバイトの区別はできていません(すべて「パート」)")
            .AppendLine("    ・給与計算表に載っていないパートの方がいる場合、正社員として扱われます")
            .AppendLine("    正しい雇用区分の一覧をいただければ差し替えます。")
            .AppendLine()
            .AppendLine("  雇用区分による判定の違い(仕様書 13.2)")
            .AppendLine("    正社員         … 予定終了は所定労働時間マスタ、無ければ拘束9時間30分")
            .AppendLine("    パート・アルバイト … 予定終了は 予定開始 + workHours(1日の拘束時間)")
            .AppendLine("                        パート・アルバイト給与計算表の出力対象になる")
            .AppendLine()
            .AppendLine("  社員番号 9000番台(gen-testdata のサンプル社員)は、再生成しても残します。")
            .AppendLine("  ")
            .ToString();

        var root = new XElement("employees", new XComment(comment));

        foreach (var e in entries.OrderBy(e => e.Employment).ThenBy(e => e.EmployeeNo.PadLeft(6, '0')))
        {
            var element = new XElement("employee",
                new XAttribute("no", e.EmployeeNo),
                new XAttribute("name", e.CanonicalName),
                new XAttribute("division", e.Division),
                new XAttribute("employment", EmployeeMaster.Label(e.Employment)));

            if (e.Department.Length > 0) element.Add(new XAttribute("department", e.Department));
            if (e.ShiftPattern.Length > 0) element.Add(new XAttribute("pattern", e.ShiftPattern));
            if (e.WorkHours is { } span)
                element.Add(new XAttribute("workHours", $"{(int)span.TotalHours}:{span.Minutes:00}"));
            if (e.HourlyWage is { } wage) element.Add(new XAttribute("hourlyWage", wage));
            element.Add(new XAttribute("managed", EmployeeMaster.ManagedText(e.IsManaged)));
            if (e.JoinedOn is { } joined) element.Add(new XAttribute("joined", joined.ToString("yyyy-MM-dd")));

            root.Add(element);
        }

        foreach (var kept in KeepTestEmployees(path)) root.Add(kept);

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), root);
        document.Save(path);
    }

    /// <summary>
    /// 出力先に既にある「社員番号 9000番台」の登録を引き継ぐ。
    ///
    /// 9000番台は gen-testdata が作るサンプル社員(実在しない)で、雇用区分ごとの判定を
    /// 確認するために手で登録している。従業員データには載らないため、
    /// 再生成のたびに消えないようここで拾い直す。実在の社員番号とは重ならない。
    /// </summary>
    private static List<XNode> KeepTestEmployees(string path)
    {
        var kept = new List<XNode>();
        if (!File.Exists(path)) return kept;

        try
        {
            var root = XDocument.Load(path).Root;
            foreach (var e in root?.Elements("employee") ?? Enumerable.Empty<XElement>())
                if (int.TryParse((string?)e.Attribute("no"), out var no) && no is >= 9000 and <= 9999)
                    kept.Add(new XElement(e));
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [注意] 既存の {Path.GetFileName(path)} を読めなかったため、" +
                              $"テストデータ用(9000番台)の登録は引き継げません: {ex.Message}");
            return new List<XNode>();
        }

        if (kept.Count > 0)
        {
            Console.WriteLine($"  テストデータ用(9000番台)の {kept.Count} 名は、そのまま残しました。");
            kept.Insert(0, new XComment(" テストデータ用(社員番号 9000番台)。gen-testdata のサンプル社員で、実在の方ではありません。 "));
        }
        return kept;
    }
}
