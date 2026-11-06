using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace HiddenReader.App.UI;

/// <summary>
/// 跨屏取色。进入时先抓一张桌面快照（物理像素，覆盖所有显示器），再在每台显示器上铺一层
/// 几乎全透明的顶层窗口接收输入：光标为十字，色块与色值显示在光标旁（避开采样点）。
/// 采样取自快照，所以覆盖层自身的像素不会污染结果；覆盖层 Alpha 为 1（肉眼不可见但可命中），
/// 因此取色点击不会落到下方应用或正文。左键确认，Esc 取消。
/// </summary>
internal sealed class ScreenColorPicker : IDisposable
{
    private readonly List<PickerOverlay> _overlays = new();
    private readonly ScreenSnapshot _snapshot;
    private DispatcherFrame? _frame;
    private Color? _result;
    private bool _finished;

    private ScreenColorPicker(ScreenSnapshot snapshot) => _snapshot = snapshot;

    /// <summary>进入取色；返回选中的颜色，Esc 或无法采样时返回 null。</summary>
    public static Color? Pick()
    {
        // 面板刚隐藏时 DWM 可能还没提交新画面，先让合成器完成一帧再抓快照。
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
        Thread.Sleep(80);

        var picker = new ScreenColorPicker(ScreenSnapshot.Capture());
        try
        {
            return picker.Run();
        }
        finally
        {
            picker.Dispose();
        }
    }

    private Color? Run()
    {
        foreach (var monitor in MonitorEnumeration.Enumerate())
        {
            var overlay = new PickerOverlay(monitor, this);
            _overlays.Add(overlay);
            overlay.Show();
        }
        if (_overlays.Count == 0) return null;

        // 焦点交给光标所在显示器：Esc 需要前台窗口，浮窗置顶时也不能被抢走。
        var active = _overlays.FirstOrDefault(o => o.ContainsCursor()) ?? _overlays[0];
        active.Activate();
        active.Focus();

        _frame = new DispatcherFrame();
        try
        {
            Dispatcher.PushFrame(_frame);
        }
        finally
        {
            _frame = null;
        }
        return _result;
    }

    internal bool TryGetCursorColor(out Color color)
    {
        color = default;
        if (!NativeMethods.GetPhysicalCursorPos(out var point)) return false;
        return _snapshot.TryGetPixel(point.X, point.Y, out color);
    }

    /// <summary>左键取色：记录颜色并结束取色循环。</summary>
    internal void Complete(Color color)
    {
        if (_finished) return;
        _finished = true;
        _result = color;
        if (_frame is not null) _frame.Continue = false;
    }

    /// <summary>Esc：放弃取色，保留面板里的草稿。</summary>
    internal void Cancel()
    {
        if (_finished) return;
        _finished = true;
        _result = null;
        if (_frame is not null) _frame.Continue = false;
    }

    public void Dispose()
    {
        _finished = true;
        foreach (var overlay in _overlays)
        {
            try { overlay.Close(); } catch { /* 关闭失败不影响恢复面板 */ }
        }
        _overlays.Clear();
        _snapshot.Dispose();
    }

    /// <summary>每台显示器一层的取色覆盖窗口。</summary>
    private sealed class PickerOverlay : Window
    {
        private readonly ScreenColorPicker _picker;
        private readonly MonitorInfo _monitor;
        private readonly Border _hud;
        private readonly Border _swatch;
        private readonly TextBlock _value;

        public PickerOverlay(MonitorInfo monitor, ScreenColorPicker picker)
        {
            _picker = picker;
            _monitor = monitor;

            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            // Alpha=1 的黑色：肉眼不可见，但覆盖层仍能命中鼠标（Alpha=0 的像素会被系统穿透）。
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            Topmost = true;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Cursor = Cursors.Cross;

            // 物理像素换算成本窗口所在显示器的 DIP；窗口完整落在一台显示器内，DPR 唯一。
            Left = monitor.Left / monitor.Scale;
            Top = monitor.Top / monitor.Scale;
            Width = monitor.Width / monitor.Scale;
            Height = monitor.Height / monitor.Scale;

            _swatch = new Border
            {
                Width = 26,
                Height = 18,
                BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x9A, 0x98, 0xA0)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(2),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _value = new TextBlock
            {
                Foreground = new SolidColorBrush(Color.FromRgb(0xE4, 0xE2, 0xDC)),
                FontFamily = new FontFamily("Consolas"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
            };
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(_swatch);
            row.Children.Add(_value);
            _hud = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x1E, 0x1F, 0x26)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x4A, 0x4C, 0x58)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 4, 8, 4),
                Child = row,
                Visibility = Visibility.Collapsed,
                IsHitTestVisible = false,
            };
            var canvas = new Canvas { Background = Brushes.Transparent };
            canvas.Children.Add(_hud);
            Content = canvas;

            MouseMove += OnMouseMove;
            MouseLeftButtonDown += OnLeftButtonDown;
            PreviewKeyDown += OnPreviewKeyDown;
            Loaded += (_, _) => RefreshHud();
        }

        public bool ContainsCursor() => NativeMethods.GetPhysicalCursorPos(out var point) && _monitor.Contains(point.X, point.Y);

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            // 焦点跟随光标所在显示器，Esc 才能被当前覆盖层收到。
            if (!IsActive) Activate();
            RefreshHud();
        }

        /// <summary>左键取色：覆盖层吃掉这次点击，下方应用与正文不会收到。</summary>
        private void OnLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            if (_picker.TryGetCursorColor(out var color)) _picker.Complete(color);
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            // 取色中的 Esc 只取消取色，不退出极简模式（浮窗收不到这次按键）。
            _picker.Cancel();
            e.Handled = true;
        }

        private void RefreshHud()
        {
            if (!NativeMethods.GetPhysicalCursorPos(out var point)) return;
            if (!_monitor.Contains(point.X, point.Y))
            {
                _hud.Visibility = Visibility.Collapsed;
                return;
            }
            if (!_picker.TryGetCursorColor(out var color)) return;

            _swatch.Background = new SolidColorBrush(color);
            _value.Text = ReadingColors.Format(color);
            _hud.Visibility = Visibility.Visible;
            _hud.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Size size = _hud.DesiredSize;

            double cursorX = (point.X - _monitor.Left) / _monitor.Scale;
            double cursorY = (point.Y - _monitor.Top) / _monitor.Scale;
            // 提示框避开采样点：默认在光标右下方，靠近边缘时翻到另一侧。
            double x = cursorX + 18;
            double y = cursorY + 18;
            if (x + size.Width > Width - 4) x = cursorX - 18 - size.Width;
            if (y + size.Height > Height - 4) y = cursorY - 18 - size.Height;
            Canvas.SetLeft(_hud, Math.Max(0, x));
            Canvas.SetTop(_hud, Math.Max(0, y));
        }
    }
}

/// <summary>一台显示器的物理像素边界与缩放。</summary>
internal readonly struct MonitorInfo
{
    public int Left { get; init; }
    public int Top { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public double Scale { get; init; }

    public bool Contains(int x, int y) => x >= Left && x < Left + Width && y >= Top && y < Top + Height;
}

internal static class MonitorEnumeration
{
    public static List<MonitorInfo> Enumerate()
    {
        var monitors = new List<MonitorInfo>();
        bool Callback(IntPtr monitor, IntPtr hdc, ref NativeMethods.RECT rect, IntPtr data)
        {
            var info = new NativeMethods.MONITORINFO { Size = Marshal.SizeOf<NativeMethods.MONITORINFO>() };
            if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return true;
            double scale = 1.0;
            if (NativeMethods.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 && dpiX > 0)
                scale = dpiX / 96.0;
            monitors.Add(new MonitorInfo
            {
                Left = info.Monitor.Left,
                Top = info.Monitor.Top,
                Width = info.Monitor.Right - info.Monitor.Left,
                Height = info.Monitor.Bottom - info.Monitor.Top,
                Scale = scale,
            });
            return true;
        }

        // 委托在调用期间保持存活。
        NativeMethods.MonitorEnumProc callback = Callback;
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, callback, IntPtr.Zero);
        return monitors;
    }
}

/// <summary>
/// 桌面快照（虚拟屏幕的物理像素）。取色在快照上读像素，避免覆盖层 Alpha 改变颜色值；
/// 抓取失败时退回实时读取桌面设备上下文。
/// </summary>
internal sealed class ScreenSnapshot : IDisposable
{
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    private IntPtr _memoryDc;
    private IntPtr _bitmap;
    private IntPtr _bits;
    private int _originX;
    private int _originY;
    private int _width;
    private int _height;

    private ScreenSnapshot() { }

    public static ScreenSnapshot Capture()
    {
        var snapshot = new ScreenSnapshot();
        snapshot.TryCapture();
        return snapshot;
    }

    private void TryCapture()
    {
        try
        {
            _originX = NativeMethods.GetSystemMetrics(SmXVirtualScreen);
            _originY = NativeMethods.GetSystemMetrics(SmYVirtualScreen);
            _width = NativeMethods.GetSystemMetrics(SmCxVirtualScreen);
            _height = NativeMethods.GetSystemMetrics(SmCyVirtualScreen);
            if (_width <= 0 || _height <= 0) return;

            IntPtr screenDc = NativeMethods.GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero) return;
            try
            {
                _memoryDc = NativeMethods.CreateCompatibleDC(screenDc);
                var header = new NativeMethods.BITMAPINFOHEADER
                {
                    Size = Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                    Width = _width,
                    Height = -_height, // 负高度 = 自上而下，行序与屏幕坐标一致
                    Planes = 1,
                    BitCount = 32,
                    Compression = 0,
                };
                var info = new NativeMethods.BITMAPINFO { Header = header, Colors = new uint[1] };
                _bitmap = NativeMethods.CreateDIBSection(_memoryDc, ref info, 0, out _bits, IntPtr.Zero, 0);
                if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
                {
                    Diagnostics.Log("screen snapshot: CreateDIBSection failed " + Marshal.GetLastWin32Error());
                    Cleanup();
                    return;
                }
                NativeMethods.SelectObject(_memoryDc, _bitmap);
                if (!NativeMethods.BitBlt(_memoryDc, 0, 0, _width, _height, screenDc, _originX, _originY, SrcCopy | CaptureBlt))
                {
                    Diagnostics.Log("screen snapshot: BitBlt failed " + Marshal.GetLastWin32Error());
                    Cleanup();
                }
            }
            finally
            {
                NativeMethods.ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        catch (Exception ex)
        {
            Diagnostics.Log("screen snapshot failed: " + ex);
            Cleanup();
        }
    }

    public bool TryGetPixel(int screenX, int screenY, out Color color)
    {
        color = default;
        if (_bits == IntPtr.Zero) return TryGetPixelLive(screenX, screenY, out color);

        int x = screenX - _originX;
        int y = screenY - _originY;
        if (x < 0 || y < 0 || x >= _width || y >= _height) return false;
        int bgra = Marshal.ReadInt32(_bits, (y * _width + x) * 4);
        color = Color.FromRgb((byte)((bgra >> 16) & 0xFF), (byte)((bgra >> 8) & 0xFF), (byte)(bgra & 0xFF));
        return true;
    }

    private static bool TryGetPixelLive(int screenX, int screenY, out Color color)
    {
        color = default;
        IntPtr dc = NativeMethods.GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return false;
        try
        {
            uint value = NativeMethods.GetPixel(dc, screenX, screenY);
            if (value == 0xFFFFFFFF) return false; // CLR_INVALID
            color = Color.FromRgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
            return true;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, dc);
        }
    }

    private void Cleanup()
    {
        if (_bitmap != IntPtr.Zero)
        {
            if (_memoryDc != IntPtr.Zero) NativeMethods.SelectObject(_memoryDc, IntPtr.Zero);
            NativeMethods.DeleteObject(_bitmap);
            _bitmap = IntPtr.Zero;
            _bits = IntPtr.Zero;
        }
        if (_memoryDc != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memoryDc);
            _memoryDc = IntPtr.Zero;
        }
    }

    public void Dispose() => Cleanup();

    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
}

internal static class NativeMethods
{
    public delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int Size;
        public RECT Monitor;
        public RECT Work;
        public int Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFOHEADER
    {
        public int Size;
        public int Width;
        public int Height;
        public short Planes;
        public short BitCount;
        public int Compression;
        public int SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public int ClrUsed;
        public int ClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BITMAPINFO
    {
        public BITMAPINFOHEADER Header;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 1)]
        public uint[] Colors;
    }

    [DllImport("user32.dll")]
    public static extern bool GetPhysicalCursorPos(out POINT point);

    [DllImport("user32.dll")]
    public static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    public static extern IntPtr GetDC(IntPtr hwnd);

    [DllImport("user32.dll")]
    public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFO info, uint usage, out IntPtr bits, IntPtr section, uint offset);

    [DllImport("gdi32.dll")]
    public static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    public static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, uint rop);

    [DllImport("gdi32.dll")]
    public static extern uint GetPixel(IntPtr dc, int x, int y);
}
