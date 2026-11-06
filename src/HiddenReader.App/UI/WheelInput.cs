using System.Runtime.InteropServices;

namespace HiddenReader.App.UI;

/// <summary>滚轮输入参数：读取系统「每格滚轮滚动的行数」设置。</summary>
internal static class WheelInput
{
    public const int DeltaPerNotch = 120;

    private const int SPI_GETWHEELSCROLLLINES = 0x0068;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfoW(uint action, uint param, out uint value, uint init);

    /// <summary>每格滚轮对应的行数（读不到系统设置时按常见默认值 3 行）。</summary>
    public static int LinesPerNotch()
    {
        try
        {
            if (SystemParametersInfoW(SPI_GETWHEELSCROLLLINES, 0, out uint lines, 0) && lines > 0)
            {
                return (int)Math.Min(lines, 30);
            }
        }
        catch { /* 读不到设置时使用默认值 */ }
        return 3;
    }
}
