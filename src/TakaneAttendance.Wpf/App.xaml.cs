using System.Windows;
using System.Windows.Threading;

namespace TakaneAttendance.Wpf;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
    }

    /// <summary>
    /// 想定外の例外でアプリが落ちると原因が分からなくなるため、内容を表示して継続する。
    /// (ヒューマンエラーとシステム不具合の切り分けを助ける)
    /// </summary>
    private static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            $"予期しないエラーが発生しました。\n\n{e.Exception.Message}\n\n{e.Exception.StackTrace}",
            "勤怠突合 PoC", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
