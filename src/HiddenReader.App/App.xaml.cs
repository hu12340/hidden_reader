using System.Windows;
using System.Windows.Threading;
using HiddenReader.App.UI;

namespace HiddenReader.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        DispatcherUnhandledException += OnUnhandled;
        Diagnostics.Log("startup args=[" + string.Join(' ', e.Args) + "]");

        var window = new MainWindow();
        MainWindow = window;
        window.Show();
        window.RestoreFloatModeIfRequested();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Diagnostics.Log("UNHANDLED: " + e.Exception);
        MessageBox.Show(e.Exception.ToString(), "HiddenReader 异常");
        e.Handled = true;
    }
}
