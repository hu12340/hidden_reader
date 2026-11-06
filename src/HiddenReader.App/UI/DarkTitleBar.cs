using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace HiddenReader.App.UI;

/// <summary>
/// 普通窗口的 Windows 11 深色标题栏：取得窗口句柄后用 DWM 显式设置标题栏背景与文字颜色。
/// 显式颜色使系统使用浅色主题时仍是深色标题栏；标题栏仍是系统原生的，最小化、
/// 最大化/还原和关闭按钮的行为不变。按激活状态调整文字亮度：失焦时略暗但保持可读。
/// </summary>
internal static class DarkTitleBar
{
    // Windows 11 的 DWM 窗口属性编号（见 DwmSetWindowAttribute）。
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaUseImmersiveDarkModeLegacy = 19;
    private const int DwmwaBorderColor = 34;
    private const int DwmwaCaptionColor = 35;
    private const int DwmwaTextColor = 36;

    private const uint SwpNosize = 0x0001;
    private const uint SwpNomove = 0x0002;
    private const uint SwpNozorder = 0x0004;
    private const uint SwpNoactivate = 0x0010;
    private const uint SwpFramechanged = 0x0020;

    // 标题栏背景与工具区同色；激活时标题文字接近正文色，失焦时略暗。
    private static readonly int CaptionColor = ToColorRef(0x1E, 0x1F, 0x26);
    private static readonly int TextActive = ToColorRef(0xE4, 0xE2, 0xDC);
    private static readonly int TextInactive = ToColorRef(0xA8, 0xA6, 0xA0);

    private static bool _captionColorSupported = true;

    /// <summary>挂接窗口：句柄就绪时立即应用，此后随激活状态刷新文字亮度。</summary>
    public static void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        window.SourceInitialized += (_, _) => Apply(helper.Handle, window.IsActive);
        window.Activated += (_, _) => Apply(helper.Handle, true);
        window.Deactivated += (_, _) => Apply(helper.Handle, false);
    }

    private static void Apply(IntPtr hwnd, bool active)
    {
        if (hwnd == IntPtr.Zero) return;

        // 深色标志让系统按深色绘制标题栏细节（含窗口按钮的前景/悬停态）。
        if (!SetAttribute(hwnd, DwmwaUseImmersiveDarkMode, 1))
            SetAttribute(hwnd, DwmwaUseImmersiveDarkModeLegacy, 1);

        // 显式颜色：系统浅色主题下也不回退到浅色标题栏。老版本不支持时保持深色标志的效果。
        if (_captionColorSupported)
        {
            bool ok = SetAttribute(hwnd, DwmwaCaptionColor, CaptionColor);
            ok &= SetAttribute(hwnd, DwmwaBorderColor, CaptionColor);
            if (!ok) _captionColorSupported = false;
        }

        // 文字颜色随激活状态变化：失焦略暗，仍与深色标题栏保持对比度。
        bool textChanged = SetAttribute(hwnd, DwmwaTextColor, active ? TextActive : TextInactive);
        // DWM 不一定立即重绘标题栏；激活状态切换时强制一次非客户区刷新。
        if (textChanged)
        {
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpFramechanged);
        }
    }

    private static bool SetAttribute(IntPtr hwnd, int attribute, int value)
    {
        try
        {
            return DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) >= 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>DWM 颜色属性使用 COLORREF（0x00BBGGRR），与 WPF 的 RGB 顺序相反。</summary>
    private static int ToColorRef(byte r, byte g, byte b) => r | (g << 8) | (b << 16);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
