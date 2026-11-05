using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HiddenReader.Core.Reading;

namespace FloatPrototype.Float;

/// <summary>
/// 浮窗原型：非分层 Win32 窗口 + DirectComposition。
/// 完全透明的像素仍属于本窗口，因此点击、右键和边缘缩放都能命中。
/// </summary>
internal sealed class FloatWindow : IDisposable
{
    private const string ClassName = "HiddenReaderFloatPrototypeWindow";

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

    private const int WM_NCCREATE = 0x0081;
    private const int WM_DESTROY = 0x0002;
    private const int WM_SIZE = 0x0005;
    private const int WM_PAINT = 0x000F;
    private const int WM_ERASEBKGND = 0x0014;
    private const int WM_SETCURSOR = 0x0020;
    private const int WM_GETMINMAXINFO = 0x0024;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_DPICHANGED = 0x02E0;

    private const int HTCLIENT = 1;

    private const int ID_TOPMOST = 100;
    private const int ID_BG_0 = 110;
    private const int ID_BG_50 = 111;
    private const int ID_BG_100 = 112;
    private const int ID_BORDER_0 = 120;
    private const int ID_BORDER_50 = 121;
    private const int ID_BORDER_100 = 122;
    private const int ID_NORMAL = 130;
    private const int ID_EXIT = 140;

    private const int TPM_RETURNCMD = 0x0100;
    private const int TPM_RIGHTBUTTON = 0x0002;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_CHECKED = 0x0008;
    private const uint MF_UNCHECKED = 0x0000;
    private const uint MF_GRAYED = 0x0001;

    private static readonly object ClassLock = new();
    private static readonly Dictionary<IntPtr, FloatWindow> Instances = new();
    private static bool _classRegistered;
    private static WndProcDelegate? _wndProc;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX wndClass);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetCapture(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursorW(IntPtr instance, IntPtr cursorName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr menu, uint flags, IntPtr id, string text);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool InvalidateRect(IntPtr hwnd, IntPtr rect, bool erase);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int code);

    [DllImport("user32.dll")]
    private static extern bool BeginPaint(IntPtr hwnd, out PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT paint);

    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const int SM_CXSIZEFRAME = 32;
    private const int SM_CYSIZEFRAME = 33;

    private static readonly IntPtr HwndTopmost = new(-1);
    private static readonly IntPtr HwndNotTopmost = new(-2);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct PAINTSTRUCT
    {
        public IntPtr Hdc;
        public bool Erase;
        public RECT Paint;
        public bool Restore;
        public bool IncUpdate;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint Size;
        public uint Style;
        public IntPtr WndProc;
        public int ClsExtra;
        public int WndExtra;
        public IntPtr Instance;
        public IntPtr Icon;
        public IntPtr Cursor;
        public IntPtr Background;
        [MarshalAs(UnmanagedType.LPWStr)] public string? MenuName;
        [MarshalAs(UnmanagedType.LPWStr)] public string ClassName;
        public IntPtr SmallIcon;
    }

    private readonly IntPtr _hwnd;
    private readonly CompositionPresenter _presenter;
    private readonly PrototypeSession _session;
    private PageLayout? _page;
    private int _layoutWidth;
    private int _layoutHeight;
    private int _selectionAnchor = -1;
    private int _selectionFocus = -1;
    private bool _selecting;
    private bool _movingWindow;
    private bool _resizing;
    private int _resizeEdges;
    private int _startCursorX;
    private int _startCursorY;
    private RECT _startWindowRect;
    private bool _trackingClick;
    private int _lastLoggedAnchor = -1;
    private int _lastLoggedWidth = -1;
    private int _downX;
    private int _downY;
    private bool _disposed;

    public bool Topmost { get; private set; } = true;
    public double BackgroundOpacity { get; private set; } = 0.85;
    public double BorderOpacity { get; private set; } = 1.0;

    public event Action? ExitToNormalRequested;
    public event Action? Closed;

    public IntPtr Handle => _hwnd;

    public FloatWindow(PrototypeSession session)
    {
        _session = session;
        EnsureClass();

        double scale = GetPrimaryDpiScale();
        var bounds = new Rect(160, 160, 460 * scale, 360 * scale);

        _hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOREDIRECTIONBITMAP,
            ClassName,
            "HiddenReader 浮窗原型",
            WS_POPUP,
            (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("创建浮窗失败: " + Marshal.GetLastWin32Error());

        GetWindowRect(_hwnd, out var createdRect);
        App.Log($"created rect={createdRect.Left},{createdRect.Top} {createdRect.Right - createdRect.Left}x{createdRect.Bottom - createdRect.Top} dpi={GetDpiForWindow(_hwnd)}");

        lock (Instances) Instances[_hwnd] = this;

        _presenter = new CompositionPresenter(_hwnd);
        _presenter.Initialize(GetDpiForWindow(_hwnd) / 96.0);
        App.Log("float window created hwnd=" + _hwnd);
        Show();
    }

    public void Show()
    {
        LogRect("before show");
        ShowWindow(_hwnd, SW_SHOW);
        SetWindowPos(_hwnd, Topmost ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW);
        LogRect("after show");
        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);
        Render();
    }

    private void LogRect(string tag)
    {
        GetWindowRect(_hwnd, out var wr);
        GetClientRect(_hwnd, out var cr);
        App.Log($"{tag}: win={wr.Right - wr.Left}x{wr.Bottom - wr.Top} client={cr.Right - cr.Left}x{cr.Bottom - cr.Top} dpi={GetDpiForWindow(_hwnd)}");
    }

    public void Hide() => ShowWindow(_hwnd, SW_HIDE);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

    private static double GetPrimaryDpiScale() => GetDpiForSystem() / 96.0;

    private static void EnsureClass()
    {
        lock (ClassLock)
        {
            if (_classRegistered) return;
            _wndProc = StaticWndProc;
            var wc = new WNDCLASSEX
            {
                Size = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                Style = 0,
                WndProc = Marshal.GetFunctionPointerForDelegate(_wndProc),
                Instance = GetModuleHandle(null),
                Cursor = LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
                ClassName = ClassName,
            };
            ushort atom = RegisterClassExW(ref wc);
            if (atom == 0) throw new InvalidOperationException("注册浮窗窗口类失败: " + Marshal.GetLastWin32Error());
            _classRegistered = true;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    private static IntPtr StaticWndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        FloatWindow? instance;
        lock (Instances) Instances.TryGetValue(hwnd, out instance);
        if (instance is not null) return instance.WndProc(hwnd, msg, wParam, lParam);
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            return HandleMessage(hwnd, msg, wParam, lParam);
        }
        catch (Exception ex)
        {
            App.Log("wndproc error " + msg + ": " + ex);
            return DefWindowProcW(hwnd, msg, wParam, lParam);
        }
    }

    private IntPtr HandleMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_SIZE:
                _layoutWidth = 0;
                Render();
                return IntPtr.Zero;
            case WM_PAINT:
                BeginPaint(hwnd, out var paint);
                EndPaint(hwnd, ref paint);
                return IntPtr.Zero;
            case WM_ERASEBKGND:
                return new IntPtr(1);
            case WM_GETMINMAXINFO:
                return IntPtr.Zero;
            case WM_SETCURSOR:
                UpdateCursor();
                return new IntPtr(1);
            case WM_LBUTTONDOWN:
                OnLeftDown(lParam);
                return IntPtr.Zero;
            case WM_MOUSEMOVE:
                OnMouseMove(lParam);
                return IntPtr.Zero;
            case WM_LBUTTONUP:
                OnLeftUp(lParam);
                return IntPtr.Zero;
            case WM_RBUTTONUP:
                OnRightUp(lParam);
                return IntPtr.Zero;
            case WM_KEYDOWN:
                OnKeyDown((int)wParam);
                return IntPtr.Zero;
            case WM_DPICHANGED:
                {
                    int suggestedW = (short)(lParam.ToInt64() & 0xFFFF);
                    int suggestedH = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                    App.Log($"wm_dpichanged wParam={wParam.ToInt64()} suggested={suggestedW}x{suggestedH}");
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, suggestedW, suggestedH, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                    _layoutWidth = 0;
                    Render();
                    return IntPtr.Zero;
                }
            case WM_DESTROY:
                Closed?.Invoke();
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void OnLeftDown(IntPtr lParam)
    {
        SetFocus(_hwnd);
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        _downX = x;
        _downY = y;

        int edges = HitTestEdges(x, y);
        if (edges != 0)
        {
            _resizing = true;
            _resizeEdges = edges;
            GetCursorPos(out var start);
            _startCursorX = start.X;
            _startCursorY = start.Y;
            GetWindowRect(_hwnd, out _startWindowRect);
            SetCapture(_hwnd);
            return;
        }

        bool ctrl = (GetKeyState(0x11) & 0x8000) != 0;
        if (ctrl)
        {
            _movingWindow = true;
            GetCursorPos(out var pt);
            _startCursorX = pt.X;
            _startCursorY = pt.Y;
            GetWindowRect(_hwnd, out _startWindowRect);
            SetCapture(_hwnd);
            return;
        }

        _trackingClick = true;
        _selecting = true;
        _selectionAnchor = HitTestText(x, y);
        _selectionFocus = _selectionAnchor;
    }

    private void OnMouseMove(IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

        if (_resizing)
        {
            ResizeTo();
            return;
        }

        if (_movingWindow)
        {
            GetCursorPos(out var pt);
            int dx = pt.X - _startCursorX;
            int dy = pt.Y - _startCursorY;
            SetWindowPos(_hwnd, IntPtr.Zero,
                _startWindowRect.Left + dx, _startWindowRect.Top + dy, 0, 0,
                SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            return;
        }

        if (_selecting)
        {
            int focus = HitTestText(x, y);
            if (focus != _selectionFocus)
            {
                _selectionFocus = focus;
                Render();
            }
        }
    }

    private void OnLeftUp(IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);

        if (_resizing)
        {
            _resizing = false;
            ReleaseCapture();
            Render();
            return;
        }

        if (_movingWindow)
        {
            _movingWindow = false;
            ReleaseCapture();
            return;
        }

        if (_selecting)
        {
            _selecting = false;
            _trackingClick = false;
            int distance = Math.Abs(x - _downX) + Math.Abs(y - _downY);
            if (distance <= 3)
            {
                _session.NextPage(ContentWidth, ContentHeight, Scale);
            }
            Render();
            return;
        }

        ReleaseCapture();
    }

    private void OnRightUp(IntPtr lParam)
    {
        int x = (short)(lParam.ToInt64() & 0xFFFF);
        int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
        ShowContextMenu(x, y);
    }

    private void OnKeyDown(int key)
    {
        switch (key)
        {
            case 0x25: // left
                _session.PreviousPage(ContentWidth, ContentHeight, Scale);
                Render();
                break;
            case 0x27: // right
                _session.NextPage(ContentWidth, ContentHeight, Scale);
                Render();
                break;
            case 0x28: // down
                _session.NextPage(ContentWidth, ContentHeight, Scale);
                Render();
                break;
            case 0x26: // up
                _session.PreviousPage(ContentWidth, ContentHeight, Scale);
                Render();
                break;
            case 0x1B: // esc
                ExitToNormalRequested?.Invoke();
                break;
            case 0x43: // C
                if ((GetKeyState(0x11) & 0x8000) != 0) CopySelection();
                break;
            case 0x74: // F5 背景不透明度 100→50→0
                BackgroundOpacity = BackgroundOpacity >= 0.99 ? 0.5 : (BackgroundOpacity <= 0.01 ? 1.0 : 0.0);
                App.Log($"opacity bg={BackgroundOpacity} border={BorderOpacity} topmost={Topmost}");
                Render();
                break;
            case 0x75: // F6 边框不透明度 100→50→0
                BorderOpacity = BorderOpacity >= 0.99 ? 0.5 : (BorderOpacity <= 0.01 ? 1.0 : 0.0);
                App.Log($"opacity bg={BackgroundOpacity} border={BorderOpacity} topmost={Topmost}");
                Render();
                break;
            case 0x76: // F7 置顶开关
                SetTopmost(!Topmost);
                App.Log($"topmost={Topmost}");
                Render();
                break;
        }
    }

    private void ShowContextMenu(int x, int y)
    {
        GetCursorPos(out var screen);
        IntPtr menu = CreatePopupMenu();
        try
        {
            AppendMenuW(menu, MF_STRING | (Topmost ? MF_CHECKED : MF_UNCHECKED), new IntPtr(ID_TOPMOST), "置顶");
            AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, string.Empty);
            AppendMenuW(menu, MF_STRING | (BackgroundOpacity <= 0.01 ? MF_CHECKED : MF_UNCHECKED), new IntPtr(ID_BG_0), "背景不透明度 0%");
            AppendMenuW(menu, MF_STRING | (Math.Abs(BackgroundOpacity - 0.5) < 0.01 ? MF_CHECKED : MF_UNCHECKED), new IntPtr(ID_BG_50), "背景不透明度 50%");
            AppendMenuW(menu, MF_STRING | (BackgroundOpacity >= 0.99 ? MF_CHECKED : MF_UNCHECKED), new IntPtr(ID_BG_100), "背景不透明度 100%");
            AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, string.Empty);
            AppendMenuW(menu, MF_STRING | (BorderOpacity <= 0.01 ? MF_CHECKED : MF_UNCHECKED), new IntPtr(ID_BORDER_0), "边框不透明度 0%");
            AppendMenuW(menu, MF_STRING | (Math.Abs(BorderOpacity - 0.5) < 0.01 ? MF_CHECKED : MF_UNCHECKED), new IntPtr(ID_BORDER_50), "边框不透明度 50%");
            AppendMenuW(menu, MF_STRING | (BorderOpacity >= 0.99 ? MF_CHECKED : MF_UNCHECKED), new IntPtr(ID_BORDER_100), "边框不透明度 100%");
            AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, string.Empty);
            AppendMenuW(menu, MF_STRING, new IntPtr(ID_NORMAL), "返回普通模式");
            AppendMenuW(menu, MF_STRING, new IntPtr(ID_EXIT), "退出");

            SetForegroundWindow(_hwnd);
            int command = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON, screen.X, screen.Y, 0, _hwnd, IntPtr.Zero);
            if (command != 0) HandleCommand(command);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void HandleCommand(int command)
    {
        App.Log("menu command=" + command);
        switch (command)
        {
            case ID_TOPMOST:
                Topmost = !Topmost;
                SetWindowPos(_hwnd, Topmost ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                break;
            case ID_BG_0: BackgroundOpacity = 0; break;
            case ID_BG_50: BackgroundOpacity = 0.5; break;
            case ID_BG_100: BackgroundOpacity = 1; break;
            case ID_BORDER_0: BorderOpacity = 0; break;
            case ID_BORDER_50: BorderOpacity = 0.5; break;
            case ID_BORDER_100: BorderOpacity = 1; break;
            case ID_NORMAL:
                ExitToNormalRequested?.Invoke();
                return;
            case ID_EXIT:
                DestroyWindow(_hwnd);
                return;
        }
        Render();
    }

    private void CopySelection()
    {
        if (_selectionAnchor < 0 || _selectionFocus < 0) return;
        int start = Math.Min(_selectionAnchor, _selectionFocus);
        int end = Math.Max(_selectionAnchor, _selectionFocus);
        if (end <= start) return;
        try { Clipboard.SetText(_session.Text[start..end]); } catch { }
    }

    private double Scale => GetDpiForWindow(_hwnd) / 96.0;

    private double ContentWidth
    {
        get
        {
            GetClientRect(_hwnd, out var rect);
            return Math.Max(1, rect.Right - rect.Left) / Scale;
        }
    }

    private double ContentHeight
    {
        get
        {
            GetClientRect(_hwnd, out var rect);
            return Math.Max(1, rect.Bottom - rect.Top) / Scale;
        }
    }

    private void Render()
    {
        GetClientRect(_hwnd, out var client);
        int width = Math.Max(1, client.Right - client.Left);
        int height = Math.Max(1, client.Bottom - client.Top);
        double scale = Scale;
        double dipWidth = width / scale;
        double dipHeight = height / scale;

        int layoutKeyW = (int)Math.Round(dipWidth);
        int layoutKeyH = (int)Math.Round(dipHeight);
        if (_page is null || layoutKeyW != _layoutWidth || layoutKeyH != _layoutHeight)
        {
            _page = Paginator.Layout(_session.Text, _session.Anchor, dipWidth, dipHeight, _session.Settings, scale);
            _layoutWidth = layoutKeyW;
            _layoutHeight = layoutKeyH;
        }

        _presenter.EnsureSize(width, height);
        _presenter.SetDpiScale(scale);
        if (_session.Anchor != _lastLoggedAnchor || width != _lastLoggedWidth)
        {
            double sample = Paginator.Measure("汉汉汉汉汉汉汉汉汉汉", _session.Settings.CreateTypeface(), _session.Settings, scale);
            App.Log($"float render anchor={_session.Anchor} page=[{_page.Start},{_page.End}) px={width:F0}x{height:F0} dip={width / scale:F0}x{height / scale:F0} dpi={scale:F2} sample10chars={sample:F1} bg={BackgroundOpacity:F2} border={BorderOpacity:F2} topmost={Topmost}");
            _lastLoggedAnchor = _session.Anchor;
            _lastLoggedWidth = width;
        }

        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            Draw(dc, dipWidth, dipHeight);
        }
        bitmap.Render(visual);
        _presenter.Present(bitmap);
    }

    private void Draw(DrawingContext dc, double width, double height)
    {
        var page = _page ?? Paginator.Layout(_session.Text, _session.Anchor, width, height, _session.Settings, Scale);

        var background = Color.FromArgb((byte)Math.Round(BackgroundOpacity * 255), 0xFA, 0xFA, 0xF7);
        dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));

        HighlightRange? selection = null;
        if (_selectionAnchor >= 0 && _selectionFocus >= 0 && _selectionAnchor != _selectionFocus)
        {
            selection = new HighlightRange(Math.Min(_selectionAnchor, _selectionFocus), Math.Abs(_selectionFocus - _selectionAnchor));
        }

        PageRenderer.DrawPage(dc, page, PageRenderer.DefaultTextBrush, null, selection);

        if (BorderOpacity > 0.001)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(BorderOpacity * 255), 0x55, 0x55, 0x55)), 1.5);
            pen.Freeze();
            double inset = 1.5;
            dc.DrawRectangle(null, pen, new Rect(inset, inset, Math.Max(0, width - inset * 2), Math.Max(0, height - inset * 2)));
        }
    }

    private int HitTestText(int x, int y)
    {
        if (_page is null) return _session.Anchor;
        double scale = Scale;
        var point = new Point(x / scale, y / scale);
        return PageRenderer.HitTest(_page, point);
    }

    private int HitTestEdges(int x, int y)
    {
        GetClientRect(_hwnd, out var client);
        int border = Math.Max(6, (int)Math.Round(6 * Scale));
        int edges = 0;
        if (x <= border) edges |= 1;
        if (x >= client.Right - border) edges |= 2;
        if (y <= border) edges |= 4;
        if (y >= client.Bottom - border) edges |= 8;
        return edges;
    }

    private void UpdateCursor()
    {
        GetCursorPos(out var pt);
        // 把屏幕坐标转换到客户区。
        GetWindowRect(_hwnd, out var wr);
        int x = pt.X - wr.Left;
        int y = pt.Y - wr.Top;
        int edges = HitTestEdges(x, y);
        IntPtr cursor = edges switch
        {
            1 or 2 => LoadCursorW(IntPtr.Zero, new IntPtr(32644)),  // IDC_SIZEWE
            4 or 8 => LoadCursorW(IntPtr.Zero, new IntPtr(32645)),  // IDC_SIZENS
            5 or 10 => LoadCursorW(IntPtr.Zero, new IntPtr(32642)), // IDC_SIZENWSE
            6 or 9 => LoadCursorW(IntPtr.Zero, new IntPtr(32643)),  // IDC_SIZENESW
            _ => LoadCursorW(IntPtr.Zero, new IntPtr(32512)),
        };
        SetCursor(cursor);
    }

    private void ResizeTo()
    {
        GetCursorPos(out var cursor);
        int dx = cursor.X - _startCursorX;
        int dy = cursor.Y - _startCursorY;
        int left = _startWindowRect.Left, top = _startWindowRect.Top;
        int right = _startWindowRect.Right, bottom = _startWindowRect.Bottom;
        if ((_resizeEdges & 1) != 0) left += dx;
        if ((_resizeEdges & 2) != 0) right += dx;
        if ((_resizeEdges & 4) != 0) top += dy;
        if ((_resizeEdges & 8) != 0) bottom += dy;

        int minWidth = (int)(220 * Scale);
        int minHeight = (int)(140 * Scale);
        if (right - left < minWidth)
        {
            if ((_resizeEdges & 1) != 0) left = right - minWidth; else right = left + minWidth;
        }
        if (bottom - top < minHeight)
        {
            if ((_resizeEdges & 4) != 0) top = bottom - minHeight; else bottom = top + minHeight;
        }

        SetWindowPos(_hwnd, IntPtr.Zero, left, top, right - left, bottom - top, SWP_NOZORDER | SWP_NOACTIVATE);
    }

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    public void SetTopmost(bool value)
    {
        Topmost = value;
        SetWindowPos(_hwnd, value ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _presenter.Dispose();
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
    }
}
