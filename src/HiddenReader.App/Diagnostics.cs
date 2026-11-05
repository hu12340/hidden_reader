using System.IO;

namespace HiddenReader.App;

/// <summary>轻量诊断日志：写入 %LOCALAPPDATA%\HiddenReader\app.log，可用 HR_LOG_DIR 覆盖。</summary>
public static class Diagnostics
{
    public static string LogPath { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("HR_LOG_DIR") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HiddenReader"),
        "app.log");

    public static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { }
    }
}
