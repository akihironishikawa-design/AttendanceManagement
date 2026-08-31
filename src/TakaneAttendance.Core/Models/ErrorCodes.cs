namespace TakaneAttendance.Core.Models;

/// <summary>メッセージの重さ。処理を止めるかどうかの判断に使う。</summary>
public enum MessageLevel
{
    /// <summary>参考。処理は続く</summary>
    Info,
    /// <summary>警告。処理は続くが確認が必要</summary>
    Warning,
    /// <summary>行エラー。その行だけ要確認にし、他は処理を続ける</summary>
    RowError,
    /// <summary>処理停止。突合を開始しない</summary>
    Fatal
}

/// <summary>
/// 処理メッセージ(統合仕様書 v3.0 第19章)。
///
/// 画面・処理ログ・実行ログのいずれにも同じ形で出せるよう、
/// コード・利用者向けの文言・発生箇所をひとまとめにして持つ。
/// </summary>
public sealed class ProcessMessage
{
    public required string Code { get; init; }
    public required MessageLevel Level { get; init; }
    /// <summary>利用者向けの表示文言(何をすればよいかが分かる書き方にする)</summary>
    public required string Text { get; init; }
    /// <summary>発生箇所(ファイル・シート・セルなど)。無い場合は空。</summary>
    public string Where { get; init; } = "";

    /// <summary>処理ログの1行。</summary>
    public override string ToString()
        => Where.Length > 0 ? $"[{Code}] {Text} ({Where})" : $"[{Code}] {Text}";
}

/// <summary>
/// エラーコードの一覧(統合仕様書 v3.0 第19.1章)。
///
/// 仕様書に載っている主要コードはそのままの記号を使い、
/// 仕様書に無いマスタ読み込みの注意はこちらで採番したコードを使う。
/// コードと文言を1か所にまとめ、画面・ログ・帳票で表現がぶれないようにする。
/// </summary>
public static class ErrorCodes
{
    // ---- 仕様書 19.1 の主要エラーコード ----
    public const string FileMissing      = "E-FILE-001";   // 必須ファイル未指定
    public const string ExcelUnreadable  = "E-XLS-001";    // Excel 読込不能
    public const string StructureMissing = "E-XLS-002";    // 必須構造未検出
    public const string NameUnresolved   = "E-NAME-001";   // 社員名未解決
    public const string ShiftDuplicated  = "E-SHIFT-001";  // 同一社員・日付のシフト重複
    public const string PunchMissing     = "W-PUNCH-001";  // 打刻0件または1件
    public const string PunchTooMany     = "W-PUNCH-002";  // 打刻3件以上
    public const string LeavePunch       = "W-LEAVE-001";  // 有給日に打刻
    public const string TripNoPunch      = "I-TRIP-001";   // 終日出張・打刻なし

    // ---- 仕様書に無い、こちらで採番したもの ----
    public const string MasterUnreadable = "E-MS-001";     // マスタXMLの書式エラー
    public const string MasterInvalid    = "W-MS-001";     // マスタの値が読めない
    public const string PeriodMismatch   = "W-DATA-001";   // 打刻とシフトの対象期間が食い違う
    public const string CountMismatch    = "W-DATA-002";   // 検算が合わない

    public static ProcessMessage Fatal(string code, string text, string where = "")
        => new() { Code = code, Level = MessageLevel.Fatal, Text = text, Where = where };

    public static ProcessMessage RowError(string code, string text, string where = "")
        => new() { Code = code, Level = MessageLevel.RowError, Text = text, Where = where };

    public static ProcessMessage Warning(string code, string text, string where = "")
        => new() { Code = code, Level = MessageLevel.Warning, Text = text, Where = where };

    public static ProcessMessage Info(string code, string text, string where = "")
        => new() { Code = code, Level = MessageLevel.Info, Text = text, Where = where };

    /// <summary>判定コードに対応する仕様書 19.1 のコード。無い場合は空。</summary>
    public static string ForResult(ResultCode code) => code switch
    {
        ResultCode.NameUnresolved => NameUnresolved,
        ResultCode.NoPunch => PunchMissing,
        ResultCode.MultiPunch => PunchTooMany,
        ResultCode.PaidLeavePunch => LeavePunch,
        ResultCode.BusinessTripFull => TripNoPunch,
        _ => ""
    };
}
