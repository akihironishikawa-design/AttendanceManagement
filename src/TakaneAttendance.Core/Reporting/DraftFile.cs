using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Reporting;

/// <summary>
/// 保留ファイル(作業途中の出席記録レポートを保存し、続きから編集するためのファイル)。
///
/// 出力した .xlsx は現行の提出様式に合わせて予定開始時刻を書かないため、読み戻しても
/// 遅刻・早退・早出・時間外の判定根拠が復元できない。そのため「続きから」は帳票の
/// 編集モデル(<see cref="ReportSheet"/>)そのものを保存する方式にしている。
///
/// 形式は JSON。人が中身を確認できるよう、日本語はそのまま・字下げ付きで書き出す。
/// </summary>
public static class DraftFile
{
    /// <summary>保留ファイルの拡張子(二重拡張子。中身が JSON であることを分かるようにしている)。</summary>
    public const string Extension = ".kintai.json";

    public const string FileFilter = "勤怠 保留ファイル|*.kintai.json|すべてのファイル|*.*";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 日本語を \uXXXX に潰さない(保留ファイルを Excel 抜きで確認できるようにするため)
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// 保留ファイルを書き出す。上書きの場合は直前の内容を .bak として残す
    /// (マスタの保存と同じ流儀。誤って別の保留で上書きしたときに戻せるようにする)。
    /// </summary>
    public static void Save(string path, DraftDocument document)
    {
        document.FormatVersion = DraftDocument.CurrentFormatVersion;
        document.SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        if (File.Exists(path)) File.Copy(path, path + ".bak", overwrite: true);

        File.WriteAllText(path, JsonSerializer.Serialize(document, JsonOptions), new UTF8Encoding(false));
    }

    /// <summary>保留ファイルを読み込む。中身が保留ファイルでない場合は理由を付けて失敗させる。</summary>
    /// <exception cref="InvalidDataException">保留ファイルとして読めない場合。</exception>
    public static DraftDocument Load(string path)
    {
        DraftDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<DraftDocument>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"保留ファイルとして読めません({ex.Message})。", ex);
        }

        if (document == null)
            throw new InvalidDataException("保留ファイルの中身が空です。");

        if (document.FormatVersion > DraftDocument.CurrentFormatVersion)
            throw new InvalidDataException(
                $"この保留ファイルは新しい形式(第{document.FormatVersion}版)です。" +
                $"このアプリは第{DraftDocument.CurrentFormatVersion}版まで読めます。アプリを更新してください。");

        document.Validate();
        return document;
    }
}

/// <summary>保留ファイルの中身。</summary>
public sealed class DraftDocument
{
    /// <summary>保留ファイルの形式。読み書きの互換性を判断するために持つ。</summary>
    // 第2版: 判定を仕様書 v3.0 の主判定(Judgement)へ変更した
    public const int CurrentFormatVersion = 2;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>保存日時(表示用)。</summary>
    public string SavedAt { get; set; } = "";

    /// <summary>保留したときの入力条件。開いたときに画面の入力欄へ戻す。</summary>
    public DraftInputs Inputs { get; set; } = new();

    public int Year { get; set; }
    public int Month { get; set; }
    /// <summary>対象月の日数(= 日列の数)。</summary>
    public int DayCount { get; set; }

    /// <summary>保留したときの処理ログ。開いたときに経緯が分かるように持ち越す。</summary>
    public List<string> Messages { get; set; } = new();

    public List<DraftEmployee> Employees { get; set; } = new();

    /// <summary>画面で編集済みのセル数(表示用。開くときは実データから数え直す)。</summary>
    public int EditedCellCount { get; set; }

    /// <summary>年月・日数の整合を確かめる。</summary>
    internal void Validate()
    {
        if (Year is < 1 or > 9999 || Month is < 1 or > 12)
            throw new InvalidDataException($"保留ファイルの対象年月が不正です({Year}年{Month}月)。");

        int days = DateTime.DaysInMonth(Year, Month);
        if (DayCount != days)
            throw new InvalidDataException(
                $"保留ファイルの日数が対象年月と合いません({Year}年{Month}月 = {days}日 / ファイル = {DayCount}日)。");
    }

    /// <summary>編集中の帳票を保留ファイルの形に写し取る。</summary>
    public static DraftDocument FromSheet(ReportSheet sheet, DraftInputs inputs, IEnumerable<string>? messages = null)
    {
        var document = new DraftDocument
        {
            Inputs = inputs,
            Year = sheet.Year,
            Month = sheet.Month,
            DayCount = sheet.DayCount,
            EditedCellCount = sheet.EditedCellCount
        };
        if (messages != null) document.Messages.AddRange(messages);

        foreach (var block in sheet.Employees)
        {
            var employee = new DraftEmployee
            {
                EmployeeNo = block.EmployeeNo,
                Name = block.Name,
                Department = block.Department,
                Key = block.Key,
                HasMatchingData = block.HasMatchingData,
                AddedFromData = block.AddedFromData,
                MetaEdited = block.MetaEdited,
                Shift = (string[])block.Shift.Clone(),
                Punch = (string[])block.Punch.Clone(),
                PlannedEnd = (string[])block.PlannedEnd.Clone(),
                Note = (string[])block.Note.Clone(),
                ShiftEdited = (bool[])block.ShiftEdited.Clone(),
                PunchEdited = (bool[])block.PunchEdited.Clone(),
                Person = DraftPerson.From(block.Person)
            };

            // 判定は開くときに計算し直すが、保留したときの内容も残しておき、
            // マスタの更新で判定が変わった場合に気付けるようにする。
            for (int i = 0; i < sheet.DayCount && i < block.Judgements.Length; i++)
            {
                var judgement = block.Judgements[i];
                if (judgement.Note.Length == 0) continue;
                employee.Judgements.Add(new DraftJudgement
                {
                    Day = i + 1,
                    Judgement = (int)judgement.Judgement,
                    Label = judgement.Label,
                    Note = judgement.Note
                });
            }

            document.Employees.Add(employee);
        }

        return document;
    }

    /// <summary>
    /// 保留ファイルから編集中の帳票を組み立て直す。
    ///
    /// 判定は保存された値をそのまま使わず、<paramref name="judge"/>(= 突合と同じルール)で
    /// 計算し直す。保留中にマスタを直した場合、その内容が反映された状態で再開できる。
    /// </summary>
    /// <param name="judge">判定器。null の場合は保存された判定をそのまま復元する。</param>
    /// <param name="changedJudgements">保留したときと判定が変わったセル数。</param>
    public ReportSheet ToSheet(ReportJudge? judge, out int changedJudgements)
    {
        changedJudgements = 0;

        var sheet = new ReportSheet { Year = Year, Month = Month, DayCount = DayCount };
        sheet.Messages.AddRange(Messages);

        foreach (var employee in Employees)
        {
            var person = employee.Person?.ToPersonRef();
            var block = new ReportEmployeeBlock(DayCount)
            {
                EmployeeNo = employee.EmployeeNo,
                Name = employee.Name,
                Department = employee.Department,
                Key = employee.Key,
                HasMatchingData = employee.HasMatchingData,
                AddedFromData = employee.AddedFromData,
                MetaEdited = employee.MetaEdited,
                Person = person
            };

            Copy(employee.Shift, block.Shift);
            Copy(employee.Punch, block.Punch);
            Copy(employee.PlannedEnd, block.PlannedEnd);
            Copy(employee.Note, block.Note);
            Copy(employee.ShiftEdited, block.ShiftEdited);
            Copy(employee.PunchEdited, block.PunchEdited);

            var saved = employee.Judgements.ToDictionary(
                j => j.Day, j => new CellJudgement((Judgement)j.Judgement, j.Label, j.Note));

            for (int i = 0; i < DayCount; i++)
            {
                var before = saved.TryGetValue(i + 1, out var s) ? s : CellJudgement.None;

                // 社員情報が無い(突合結果に現れなかった)社員は判定し直せないため、保存値をそのまま使う
                if (judge == null || person == null)
                {
                    block.Judgements[i] = before;
                    continue;
                }

                var after = judge.Evaluate(person, new DateOnly(Year, Month, i + 1),
                                           block.Shift[i], block.Punch[i], block.PlannedEnd[i]);
                block.Judgements[i] = after;
                if (after.Judgement != before.Judgement || after.Note != before.Note) changedJudgements++;
            }

            sheet.Employees.Add(block);
        }

        return sheet;
    }

    /// <summary>保留ファイル側の配列が短い・長い場合でも取り込めるようにする。</summary>
    private static void Copy<T>(T[]? source, T[] destination)
    {
        if (source == null) return;
        Array.Copy(source, destination, Math.Min(source.Length, destination.Length));
    }
}

/// <summary>保留したときの入力条件。</summary>
public sealed class DraftInputs
{
    public string ShiftPath { get; set; } = "";
    public string ShiftSheetName { get; set; } = "";
    public string PunchPath { get; set; } = "";
    public string PunchSheetName { get; set; } = "";
    public string TemplatePath { get; set; } = "";
    public string MastersDirectory { get; set; } = "";
    public bool AutoDetectYearMonth { get; set; } = true;
    public bool OnlyPersonsInShift { get; set; } = true;
}

/// <summary>保留ファイルの社員1件分。<see cref="ReportEmployeeBlock"/> と対応する。</summary>
public sealed class DraftEmployee
{
    public string EmployeeNo { get; set; } = "";
    public string Name { get; set; } = "";
    public string Department { get; set; } = "";
    public string Key { get; set; } = "";
    public bool HasMatchingData { get; set; }
    public bool AddedFromData { get; set; }
    public bool MetaEdited { get; set; }

    /// <summary>シフト行。添字0 = 1日。</summary>
    public string[] Shift { get; set; } = Array.Empty<string>();
    /// <summary>打刻行。添字0 = 1日。</summary>
    public string[] Punch { get; set; } = Array.Empty<string>();
    /// <summary>予定終了時刻。添字0 = 1日。第2版から。</summary>
    public string[] PlannedEnd { get; set; } = Array.Empty<string>();
    /// <summary>備考。添字0 = 1日。第2版から。</summary>
    public string[] Note { get; set; } = Array.Empty<string>();

    public bool[] ShiftEdited { get; set; } = Array.Empty<bool>();
    public bool[] PunchEdited { get; set; } = Array.Empty<bool>();

    /// <summary>判定の付いた日だけを持つ(矛盾なしの日は書かない)。</summary>
    public List<DraftJudgement> Judgements { get; set; } = new();

    /// <summary>突合に使った社員情報。判定し直すために必要。</summary>
    public DraftPerson? Person { get; set; }
}

/// <summary>保留したときの判定(1日分)。</summary>
public sealed class DraftJudgement
{
    /// <summary>日(1始まり)。</summary>
    public int Day { get; set; }
    /// <summary>主判定(<see cref="Models.Judgement"/> の値)。第2版から。</summary>
    public int Judgement { get; set; }
    /// <summary>判定結果行の文言(要確認 / 打刻漏れ / 遅 など)。第2版から。</summary>
    public string Label { get; set; } = "";
    public string Note { get; set; } = "";
}

/// <summary><see cref="PersonRef"/> の保存用。</summary>
public sealed class DraftPerson
{
    public string SourceName { get; set; } = "";
    public string NormalizedName { get; set; } = "";
    public string? CanonicalName { get; set; }
    public string? Department { get; set; }
    public string? EmployeeNo { get; set; }
    public string Key { get; set; } = "";
    /// <summary>雇用区分(<see cref="EmploymentType"/> の値)。第2版から。</summary>
    public int Employment { get; set; }

    public static DraftPerson? From(PersonRef? person) => person == null ? null : new DraftPerson
    {
        Employment = (int)person.Employment,
        SourceName = person.SourceName,
        NormalizedName = person.NormalizedName,
        CanonicalName = person.CanonicalName,
        Department = person.Department,
        EmployeeNo = person.EmployeeNo,
        Key = person.Key
    };

    public PersonRef ToPersonRef() => new()
    {
        SourceName = SourceName,
        NormalizedName = NormalizedName,
        CanonicalName = CanonicalName,
        Department = Department,
        EmployeeNo = EmployeeNo,
        Key = Key,
        Employment = (EmploymentType)Employment
    };
}
