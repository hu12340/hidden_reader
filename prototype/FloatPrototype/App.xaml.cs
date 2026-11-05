using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace FloatPrototype;

public partial class App : Application
{
    public static bool AutoFloat { get; private set; }

    public static string LogPath { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("HR_PROTO_LOG_DIR") ?? Path.GetTempPath(), "hiddenreader-proto.log");

    public static void Log(string message)
    {
        try { File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}"); } catch { }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AutoFloat = e.Args.Any(a => a.Equals("--float", StringComparison.OrdinalIgnoreCase));
        DispatcherUnhandledException += OnUnhandled;
        Log("startup");
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("UNHANDLED: " + e.Exception);
        MessageBox.Show(e.Exception.ToString(), "浮窗原型异常");
        e.Handled = true;
    }
}
