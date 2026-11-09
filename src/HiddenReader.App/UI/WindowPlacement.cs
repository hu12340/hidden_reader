using System.Runtime.InteropServices;

namespace HiddenReader.App.UI;

/// <summary>
/// 多屏窗口恢复辅助：全部判断在物理像素坐标系中进行（跨 DPI 一致），
/// 用于区分「尚未保存位置」与有效坐标，并在保存位置不可见时回退到可见工作区。
/// </summary>
internal static class WindowPlacement
{
    public const double LegacyUnplacedValue = -1; // 旧版 library.json 用 -1 表示「尚未放置」

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;

        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int CbSize;
        public RECT Monitor;
        public RECT WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>旧版「尚未保存」判定：两个坐标都等于 -1（或缺失/NaN）视为未放置。</summary>
    public static bool IsUnplaced(double? x, double? y)
    {
        if (x is null || y is null) return true;
        if (double.IsNaN(x.Value) || double.IsNaN(y.Value)) return true;
        return x.Value == LegacyUnplacedValue && y.Value == LegacyUnplacedValue;
    }

    /// <summary>浮窗旧版「尚未放置」判定：X/Y 同时等于 -1。</summary>
    public static bool IsUnplaced(double x, double y)
    {
        if (double.IsNaN(x) || double.IsNaN(y)) return true;
        return x == LegacyUnplacedValue && y == LegacyUnplacedValue;
    }

    /// <summary>取物理矩形与最近显示器工作区的交集是否足以视为可见。</summary>
    public static bool IsRectVisibleInWorkArea(RECT window, RECT workArea, int minVisiblePx = 80)
    {
        int ix = Math.Min(window.Right, workArea.Right) - Math.Max(window.Left, workArea.Left);
        int iy = Math.Min(window.Bottom, workArea.Bottom) - Math.Max(window.Top, workArea.Top);
        return ix >= Math.Min(minVisiblePx, window.Width) && iy >= Math.Min(minVisiblePx, window.Height);
    }

    /// <summary>返回包含该矩形最近的显示器的工作区（物理像素）；查询失败返回 false。</summary>
    public static bool TryGetWorkAreaForRect(RECT rect, out RECT workArea)
    {
        IntPtr monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONEAREST);
        return TryGetWorkArea(monitor, out workArea);
    }

    public static bool TryGetWorkArea(IntPtr hMonitor, out RECT workArea)
    {
        workArea = default;
        if (hMonitor == IntPtr.Zero) return false;
        var info = new MONITORINFO { CbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(hMonitor, ref info)) return false;
        workArea = info.WorkArea;
        return true;
    }

    /// <summary>
    /// 校验已存在的窗口（普通窗口）是否仍落在某个可见工作区内；
    /// 不可见时把窗口移入最近显示器的工作区。返回是否发生了回退移动。
    /// </summary>
    public static bool EnsureWindowVisible(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        if (!GetWindowRect(hwnd, out var rect)) return false;
        if (!TryGetWorkAreaForRect(rect, out var workArea)) return false;
        if (IsRectVisibleInWorkArea(rect, workArea)) return false;

        // 回退：保持窗口尺寸，放回最近显示器工作区内（物理坐标）。
        double dpi = GetDpiForWindow(hwnd) / 96.0;
        if (dpi <= 0) dpi = 1.0;
        int w = Math.Min(rect.Width, Math.Max(100, workArea.Width - 32));
        int h = Math.Min(rect.Height, Math.Max(80, workArea.Height - 32));
        int x = Math.Clamp(rect.Left, workArea.Left + 8, Math.Max(workArea.Left + 8, workArea.Right - w - 8));
        int y = Math.Clamp(rect.Top, workArea.Top + 8, Math.Max(workArea.Top + 8, workArea.Bottom - h - 8));

        SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h,
            SWP_NOZORDER | SWP_NOACTIVATE);
        return true;
    }

    /// <summary>面板放在宿主所在屏幕的右下角。若会挡住浮窗正文，优先放到浮窗旁边。</summary>
    public static void PlaceReadingPanel(IntPtr panel, IntPtr owner, bool avoidOwner)
    {
        if (!GetWindowRect(owner, out var ownerRect) || !TryGetWorkAreaForRect(ownerRect, out var workArea)
            || !GetWindowRect(panel, out var panelRect)) return;

        int margin = (int)Math.Round(16 * GetDpiForWindow(panel) / 96.0);
        int x = Math.Max(workArea.Left + margin, workArea.Right - panelRect.Width - margin);
        int y = Math.Max(workArea.Top + margin, workArea.Bottom - panelRect.Height - margin);
        if (avoidOwner && x < ownerRect.Right && x + panelRect.Width > ownerRect.Left
            && y < ownerRect.Bottom && y + panelRect.Height > ownerRect.Top)
        {
            int leftOfOwner = ownerRect.Left - panelRect.Width - margin;
            int aboveOwner = ownerRect.Top - panelRect.Height - margin;
            if (leftOfOwner >= workArea.Left + margin) x = leftOfOwner;
            else if (aboveOwner >= workArea.Top + margin) y = aboveOwner;
        }

        SetWindowPos(panel, IntPtr.Zero, x, y, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>把物理矩形回退到指定工作区内（右下角对齐优先），返回调整后的矩形。</summary>
    public static RECT ClampIntoWorkArea(RECT rect, RECT workArea, int margin = 24)
    {
        int w = Math.Min(rect.Width, Math.Max(100, workArea.Width - margin * 2));
        int h = Math.Min(rect.Height, Math.Max(80, workArea.Height - margin * 2));
        int x = Math.Clamp(rect.Left, workArea.Left + margin, Math.Max(workArea.Left + margin, workArea.Right - w - margin));
        int y = Math.Clamp(rect.Top, workArea.Top + margin, Math.Max(workArea.Top + margin, workArea.Bottom - h - margin));
        return new RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
    }
}
