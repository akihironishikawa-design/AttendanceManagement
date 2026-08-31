using System.Windows.Controls;

namespace TakaneAttendance.Wpf.Views;

/// <summary>
/// 出席記録レポート画面の日列。
/// 中身は <see cref="ReportGridBuilder"/> が組み立てる3段(シフト / 出勤 / 退勤)のテンプレート。
/// 値の変更は <see cref="CellEditorWindow"/> で行うため、セルへの直接入力は受け付けない。
/// </summary>
internal sealed class DayCellColumn : DataGridTemplateColumn
{
    public DayCellColumn(int dayIndex) => DayIndex = dayIndex;

    /// <summary>この列が受け持つ日(0始まり = 1日)。</summary>
    public int DayIndex { get; }
}
