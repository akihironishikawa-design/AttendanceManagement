using System.Security.Cryptography;
using System.Text;
using TakaneAttendance.Core.Models;

namespace TakaneAttendance.Core.Reporting;

/// <summary>入力ファイル1件分の記録(統合仕様書 v3.0 第18.2章「入力」)。</summary>
public sealed class LogInputFile
{
    public required string Role { get; init; }        // シフト表 / 打刻データ / テンプレート
    public required string Path { get; init; }
    public string SheetName { get; init; } = "";
    public long Size { get; init; }
    public DateTime? UpdatedAt { get; init; }
    public string Hash { get; init; } = "";

    /// <summary>取り込んだファイルの控えを作る。同じ入力かどうかをハッシュで確かめられるようにする。</summary>
    public static LogInputFile From(string role, string path, string sheetName = "")
    {
        if (!File.Exists(path))
            return new LogInputFile { Role = role, Path = path, SheetName = sheetName };

        var info = new FileInfo(path);
        return new LogInputFile
        {
            Role = role,
            Path = path,
            SheetName = sheetName,
            Size = info.Length,
            UpdatedAt = info.LastWriteTime,
            Hash = ComputeHash(path)
        };
    }

    private static string ComputeHash(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream))[..16];
        }
        catch (IOException)
        {
            return "(読めません)";
        }
    }
}

/// <summary>
/// 実行ログ(統合仕様書 v3.0 第18.2章)。
///
/// 「入力・判定・修正・出力を実行IDで追跡できる」ことが受入条件(第20章 追跡性)のため、
/// 1回の突合につき1ファイルを残す。締めの説明を後から求められたときの根拠になる。
///
/// ファイル名は 実行ログ_20260827_143000_a1b2c3.txt。
/// 中身は人が読める書式にしている(お客様が Excel を持たない環境でも開けるようにするため)。
/// </summary>
public static class ExecutionLog
{
    /// <summary>既定の保存先(実行ファイルと同階層の ログ フォルダ)。</summary>
    public static string DefaultDirectory => Path.Combine(AppContext.BaseDirectory, "ログ");

    /// <summary>
    /// 実行ログを書き出し、書き出したパスを返す。
    /// </summary>
    /// <param name="directory">保存先フォルダ。無ければ作る。</param>
    /// <param name="inputs">取り込んだ入力ファイル</param>
    /// <param name="outputs">出力した帳票・ファイル</param>
    /// <param name="edits">画面での修正履歴</param>
    public static string Write(
        string directory,
        MatchingResult result,
        IReadOnlyList<LogInputFile> inputs,
        IReadOnlyList<string>? outputs = null,
        IReadOnlyList<EditHistoryEntry>? edits = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"実行ログ_{result.ExecutionId}.txt");

        var sb = new StringBuilder();

        Section(sb, "実行");
        Line(sb, "実行ID", result.ExecutionId);
        Line(sb, "アプリ版", AppVersion);
        Line(sb, "開始", result.StartedAt.ToString("yyyy/MM/dd HH:mm:ss"));
        Line(sb, "終了", result.FinishedAt.ToString("yyyy/MM/dd HH:mm:ss"));
        Line(sb, "処理時間", $"{result.Elapsed.TotalMilliseconds:0} ms");
        Line(sb, "利用者", Environment.UserName);
        Line(sb, "対象年月", $"{result.TargetYear}年{result.TargetMonth}月");
        Line(sb, "マスタ", result.Masters?.Directory ?? "(既定)");

        Section(sb, "入力");
        foreach (var f in inputs)
        {
            sb.AppendLine($"  {f.Role}");
            Line(sb, "  ファイル", f.Path);
            if (f.SheetName.Length > 0) Line(sb, "  シート", f.SheetName);
            if (f.Size > 0) Line(sb, "  サイズ", $"{f.Size:#,0} バイト");
            if (f.UpdatedAt is { } at) Line(sb, "  更新日時", at.ToString("yyyy/MM/dd HH:mm:ss"));
            if (f.Hash.Length > 0) Line(sb, "  ハッシュ", f.Hash);
        }
        Line(sb, "シフト読込", $"{result.ShiftRecordCount} 件");
        Line(sb, "打刻読込", $"{result.PunchRecordCount} 件");

        Section(sb, "結果");
        Line(sb, "突合明細", $"{result.Details.Count} 件 / 対象 {result.PersonCount} 名");
        Line(sb, "正常", $"{result.NormalCount} 件");
        Line(sb, "遅刻", $"{result.LateCount} 件");
        Line(sb, "早退", $"{result.EarlyLeaveCount} 件");
        Line(sb, "早出", $"{result.EarlyInCount} 件");
        Line(sb, "時間外", $"{result.OvertimeCount} 件");
        Line(sb, "要確認", $"{result.ReviewCount} 件");
        Line(sb, "対象外", $"{result.ExcludedCount} 件");

        sb.AppendLine();
        sb.AppendLine("  判定コード別");
        foreach (var g in result.Details.SelectMany(d => d.ResultCodes)
                                        .GroupBy(c => c)
                                        .OrderByDescending(g => g.Count()))
            sb.AppendLine($"    {ResultCodeInfo.CodeName(g.Key),-20} {g.Count(),6} 件");

        Section(sb, "明細(エラー・警告)");
        if (result.ProcessMessages.Count == 0) sb.AppendLine("  なし");
        foreach (var m in result.ProcessMessages)
            sb.AppendLine($"  {m.Level,-8} {m}");

        if (result.UnresolvedNames.Count > 0)
        {
            Section(sb, "氏名未解決");
            foreach (var u in result.UnresolvedNames)
                sb.AppendLine($"  [{ErrorCodes.NameUnresolved}] {u.Origin} 「{u.SourceName}」 {u.Occurrences} 件 {u.Department}");
        }

        Section(sb, "修正履歴");
        if (edits == null || edits.Count == 0) sb.AppendLine("  なし");
        else
            foreach (var e in edits)
                sb.AppendLine("  " + e.ToLogLine());

        Section(sb, "出力");
        if (outputs == null || outputs.Count == 0) sb.AppendLine("  なし");
        else
            foreach (var o in outputs) sb.AppendLine($"  {o}");

        sb.AppendLine();
        sb.AppendLine("  ※ このログは締め処理の根拠として残しています。");
        sb.AppendLine("     入力ファイルのハッシュが同じであれば、同じ入力から同じ判定が得られます。");

        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        return path;
    }

    private static string AppVersion =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0.0";

    private static void Section(StringBuilder sb, string title)
    {
        sb.AppendLine();
        sb.AppendLine($"================ {title} ================");
    }

    private static void Line(StringBuilder sb, string label, string value)
        => sb.AppendLine($"  {label,-12} : {value}");
}
