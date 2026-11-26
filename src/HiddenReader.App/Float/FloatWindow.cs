using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using HiddenReader.App.Data;
using HiddenReader.App.UI;
using HiddenReader.Core.Reading;

namespace HiddenReader.App.Float;

/// <summary>
/// 极简浮窗：非分层 Win32 窗口 + DirectComposition（原型验证通过的方案）。
/// 完全透明的像素仍属于本窗口，因此点击、右键和边缘缩放都能命中。
/// </summary>
internal sealed class FloatWindow : IDisposable
{
    private const string ClassName = "HiddenReaderFloatWindow";

    private const int WS_POPUP = unchecked((int)0x80000000);
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_NOREDIRECTIONBITMAP = 0x00200000;

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
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_DPICHANGED = 0x02E0;

    private const int ID_TOPMOST = 100;
    private const int ID_BG_OPACITY = 110;
    private const int ID_BORDER_OPACITY = 120;
    private const int ID_COLORS = 125;
    private const int ID_FONT_SIZE = 126;
    private const int ID_NORMAL = 130;
    private const int ID_EXIT = 140;

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
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForSystem();

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
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool SetFocus(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int key);

    /// <summary>异步键状态：用于判断「菜单关闭的这一刻」左键是否真按下（不受消息队列影响）。</summary>
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    /// <summary>前台窗口：失活关闭时用来区分「切到浮窗」和「切到其他应用」。</summary>
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    private const int SW_SHOW = 5;
    private const int SW_HIDE = 0;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;

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
    private readonly ReadingSession _session;
    private PageLayout? _page;
    /// <summary>生成 _page 时使用的锚点。尺寸不变但锚点已变（有效翻页）时必须重新排版，
    /// 否则浮窗会一直显示旧页——这正是固定尺寸浮窗翻页不刷新的根源。</summary>
    private int _laidAnchor = -1;
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
    private int _downX;
    private int _downY;
    private bool _disposed;
    /// <summary>当前打开的自绘菜单。</summary>
    private FloatMenuWindow? _menu;
    private bool _menuOpen;
    /// <summary>
    /// 菜单因失活关闭时记录的「致关闭的那次左键按下」的屏幕坐标；null 表示没有这样的按下。
    /// 点击浮窗本体时激活转移先于 WM_LBUTTONDOWN，本次按下只负责关菜单，不得进入选区/翻页路径。
    /// 只有关闭那一刻左键确实按下且光标在浮窗内才记录：Alt+Tab 或点击其他应用同样让菜单失活，
    /// 但没有按下落到浮窗，记录后会吞掉返回浮窗后的第一次独立点击（审查缺陷 #5）。
    /// 命中一次即清空，之后的独立点击正常翻页。
    /// </summary>
    private POINT? _menuClosePressPoint;
    private const int VkLeftButton = 0x01;
    /// <summary>正文文字画刷：来自应用级阅读配色，与普通模式共用。</summary>
    private SolidColorBrush _textBrush = CreateBrush(ReadingColors.Text);
    /// <summary>高亮调色板随配色解析（亮色背景用正文色派生，审查缺陷 #4），与普通模式一致。</summary>
    private PageRenderer.HighlightPalette _highlights =
        PageRenderer.ResolveHighlightPalette(ReadingColors.Background, ReadingColors.Text);

    public bool Topmost { get; private set; }
    /// <summary>已确认的背景不透明度（0-1）。面板草稿期间 CaptureState 仍返回确认值，
    /// 因此预览期间翻页等其他状态保存只写已确认值（UI-SPEC UI-03）。</summary>
    public double BackgroundOpacity { get; private set; }
    /// <summary>已确认的边框不透明度（0-1）。</summary>
    public double BorderOpacity { get; private set; }
    /// <summary>不透明度面板草稿：非 null 时渲染用草稿值，「确定」转正、「取消」丢弃。</summary>
    private (double Background, double Border)? _opacityDraft;

    /// <summary>返回普通模式请求：宿主隐藏浮窗回到普通模式，浮窗对象保留等待再次进入或退出。</summary>
    public event Action? ReturnToNormalRequested;
    /// <summary>最小化整个软件请求：宿主隐藏浮窗并把主窗口以最小化态放入任务栏。</summary>
    public event Action? MinimizeRequested;
    /// <summary>用户选择退出：宿主应保存状态并关闭应用。</summary>
    public event Action? ExitRequested;
    /// <summary>任何交互后触发，宿主可保存状态。</summary>
    public event Action? StateChanged;
    /// <summary>右键菜单请求打开阅读配色面板：宿主以浮窗为宿主窗口打开同一个面板。</summary>
    public event Action? ReadingColorsRequested;
    public event Action? ReadingFontSizeRequested;
    /// <summary>右键菜单请求打开不透明度面板：参数为真时初始聚焦背景滑杆，否则聚焦边框。</summary>
    public event Action<bool>? OpacityPanelRequested;

    public IntPtr Handle => _hwnd;

    public FloatWindow(ReadingSession session, FloatState state)
    {
        _session = session;
        BackgroundOpacity = Math.Clamp(state.BackgroundOpacity, 0, 1);
        BorderOpacity = Math.Clamp(state.BorderOpacity, 0, 1);
        Topmost = state.Topmost;
        EnsureClass();

        double scale = GetDpiForSystem() / 96.0;
        double dipWidth = Math.Max(220, state.Width);
        double dipHeight = Math.Max(140, state.Height);
        double dipX = state.X, dipY = state.Y;
        if (WindowPlacement.IsUnplaced(dipX, dipY))
        {
            // 未放置过：放到工作区右下角。
            var work = SystemParameters.WorkArea;
            dipX = Math.Max(8, work.Right - dipWidth - 24);
            dipY = Math.Max(8, work.Bottom - dipHeight - 24);
        }
        int x = (int)Math.Round(dipX * scale);
        int y = (int)Math.Round(dipY * scale);
        int w = (int)Math.Round(dipWidth * scale);
        int h = (int)Math.Round(dipHeight * scale);

        // 在物理像素坐标系核对保存位置：显示器断开或坐标越界时，
        // 回退到最近显示器工作区（有效负坐标不受影响）。
        var desired = new WindowPlacement.RECT { Left = x, Top = y, Right = x + w, Bottom = y + h };
        if (WindowPlacement.TryGetWorkAreaForRect(desired, out var workArea)
            && !WindowPlacement.IsRectVisibleInWorkArea(desired, workArea))
        {
            var fallback = WindowPlacement.ClampIntoWorkArea(desired, workArea);
            Diagnostics.Log($"float position fallback: ({x},{y}) -> ({fallback.Left},{fallback.Top})");
            x = fallback.Left; y = fallback.Top; w = fallback.Width; h = fallback.Height;
        }

        _hwnd = CreateWindowExW(
            (uint)(WS_EX_TOOLWINDOW | (Topmost ? WS_EX_TOPMOST : 0) | WS_EX_NOREDIRECTIONBITMAP),
            ClassName,
            "HiddenReader 浮窗",
            WS_POPUP,
            x, y, w, h,
            IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("创建浮窗失败: " + Marshal.GetLastWin32Error());

        lock (Instances) Instances[_hwnd] = this;

        _presenter = new CompositionPresenter(_hwnd);
        _presenter.Initialize(GetDpiForWindow(_hwnd) / 96.0);
        ReadingColors.Changed += OnColorsChanged;
        Diagnostics.Log($"float window created hwnd={_hwnd} dpi={GetDpiForWindow(_hwnd)}");
    }

    /// <summary>
    /// 配色变化只提交新帧：_page 与锚点保留，不重排，阅读位置不变。
    /// 浮窗背景的 Alpha 仍由背景不透明度决定，文字与边框的不透明度各自独立。
    /// </summary>
    private void OnColorsChanged()
    {
        if (_disposed) return;
        _textBrush = CreateBrush(ReadingColors.Text);
        _highlights = PageRenderer.ResolveHighlightPalette(ReadingColors.Background, ReadingColors.Text);
        Render();
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void Show()
    {
        ShowWindow(_hwnd, SW_SHOW);
        ApplyTopmost();
        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);
        Render();
    }

    public void Hide() => ShowWindow(_hwnd, SW_HIDE);

    public void Relayout()
    {
        _page = null;
        if (IsWindowVisible(_hwnd)) Render();
    }

    private void ApplyTopmost()
    {
        SetWindowPos(_hwnd, Topmost ? HwndTopmost : HwndNotTopmost, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void SetTopmost(bool value)
    {
        Topmost = value;
        ApplyTopmost();
        Render();
        StateChanged?.Invoke();
    }

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
            Diagnostics.Log("float wndproc error " + msg + ": " + ex);
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
                BeginPaintSafe(hwnd);
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
                ShowContextMenu();
                return IntPtr.Zero;
            case WM_KEYDOWN:
                OnKeyDown((int)wParam);
                return IntPtr.Zero;
            case WM_MOUSEWHEEL:
                // Windows「悬停时滚动非活动窗口」开启（默认）时，滚轮无需焦点即可送达；
                // 关闭时需要先点一下浮窗取得焦点。
                OnMouseWheel((short)((wParam.ToInt64() >> 16) & 0xFFFF));
                return IntPtr.Zero;
            case WM_DPICHANGED:
                {
                    int suggestedW = (short)(lParam.ToInt64() & 0xFFFF);
                    int suggestedH = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                    SetWindowPos(hwnd, IntPtr.Zero, 0, 0, suggestedW, suggestedH, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
                    _layoutWidth = 0;
                    Render();
                    return IntPtr.Zero;
                }
            case WM_DESTROY:
                return IntPtr.Zero;
        }
        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    private void BeginPaintSafe(IntPtr hwnd)
    {
        BeginPaint(hwnd, out var paint);
        EndPaint(hwnd, ref paint);
    }

    [DllImport("user32.dll")]
    private static extern bool BeginPaint(IntPtr hwnd, out PAINTSTRUCT paint);

    [DllImport("user32.dll")]
    private static extern bool EndPaint(IntPtr hwnd, ref PAINTSTRUCT paint);

    private void OnLeftDown(IntPtr lParam)
    {
        if (_menuOpen)
        {
            // 菜单开着：本次按下只负责关菜单，不进入选区/翻页路径。
            _menu?.Close();
            // Close() 内已按「失活关闭」记录过这次按下的坐标；既然本次按下已在这里被吞，
            // 记录必须作废，否则同一位置的下一击（用户常在同一处继续翻页）会再被吞一次。
            _menuClosePressPoint = null;
            return;
        }
        if (_menuClosePressPoint is POINT closingPress)
        {
            _menuClosePressPoint = null;
            // 菜单刚因「落在浮窗内的左键按下」失活关闭：本次按下就是导致关闭的那次点击，
            // 吞掉它，紧随其后的独立点击正常翻页（审查缺陷 #5）。
            // 坐标与记录不同则说明是之后的一次独立点击（记录的那次没有派发到这里），正常处理。
            if (GetCursorPos(out var now)
                && Math.Abs(now.X - closingPress.X) <= 3 && Math.Abs(now.Y - closingPress.Y) <= 3)
            {
                Diagnostics.Log("float menu closing press swallowed");
                return;
            }
        }
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
            StateChanged?.Invoke();
            return;
        }

        if (_movingWindow)
        {
            _movingWindow = false;
            ReleaseCapture();
            StateChanged?.Invoke();
            return;
        }

        if (_selecting)
        {
            _selecting = false;
            int distance = Math.Abs(x - _downX) + Math.Abs(y - _downY);
            if (distance <= 3)
            {
                // 单击：翻到下一页，同时清除旧选区。
                _selectionAnchor = -1;
                _selectionFocus = -1;
                _session.NextPage(ContentWidth, ContentHeight, Scale);
                StateChanged?.Invoke();
            }
            // 拖动选字：松手后保留高亮，供 Ctrl+C 复制；不翻页。
            Render();
            return;
        }

        ReleaseCapture();
    }

    private void OnKeyDown(int key)
    {
        // 键盘输入说明致关闭的那次按下并没有派发到这里（Esc/命令/切走后返回）：
        // 清掉记录，避免残留标记吞掉之后的正文点击。
        _menuClosePressPoint = null;
        switch (key)
        {
            case 0x25: // left
            case 0x26: // up
                ClearSelection();
                _session.PreviousPage(ContentWidth, ContentHeight, Scale);
                StateChanged?.Invoke();
                Render();
                break;
            case 0x27: // right
            case 0x28: // down
                ClearSelection();
                _session.NextPage(ContentWidth, ContentHeight, Scale);
                StateChanged?.Invoke();
                Render();
                break;
            case 0x0D: // enter
                ReturnToNormalRequested?.Invoke();
                break;
            case 0x1B: // esc
                // Esc 直接最小化整个软件（不再弹主界面）；返回普通模式改由 Enter 或菜单承担。
                MinimizeRequested?.Invoke();
                break;
            case 0x43: // C
                if ((GetKeyState(0x11) & 0x8000) != 0) CopySelection();
                break;
            case 0x74: // F5 背景不透明度 100→50→0（三档快捷方式保留）
                _opacityDraft = null; // 确认值变化后丢弃可能残留的面板草稿
                BackgroundOpacity = BackgroundOpacity >= 0.99 ? 0.5 : (BackgroundOpacity <= 0.01 ? 1.0 : 0.0);
                Diagnostics.Log($"opacity bg={BackgroundOpacity} border={BorderOpacity} topmost={Topmost}");
                StateChanged?.Invoke();
                Render();
                break;
            case 0x75: // F6 边框不透明度 100→50→0（三档快捷方式保留）
                _opacityDraft = null;
                BorderOpacity = BorderOpacity >= 0.99 ? 0.5 : (BorderOpacity <= 0.01 ? 1.0 : 0.0);
                Diagnostics.Log($"opacity bg={BackgroundOpacity} border={BorderOpacity} topmost={Topmost}");
                StateChanged?.Invoke();
                Render();
                break;
            case 0x76: // F7 置顶开关
                SetTopmost(!Topmost);
                break;
        }
    }

    /// <summary>滚轮连续滚动正文，可停在整页边界之间；与普通模式共用 ReadingSession 滚动语义。</summary>
    private void OnMouseWheel(int delta)
    {
        // 滚轮是独立输入：清掉菜单关闭时残留的按下记录。
        _menuClosePressPoint = null;
        int notches = delta / 120;
        if (notches == 0 && delta != 0) notches = Math.Sign(delta);
        if (notches == 0) return;
        int linesPerNotch = WheelInput.LinesPerNotch();
        int lines = Math.Clamp(Math.Abs(notches) * linesPerNotch, 1, 30);
        // 与普通窗口同一符号约定：负增量（朝自己）看到后文、文字上移。
        bool moved = notches < 0
            ? _session.ScrollDownLines(lines, ContentWidth, ContentHeight, Scale)
            : _session.ScrollUpLines(lines, ContentWidth, ContentHeight, Scale);
        if (moved)
        {
            ClearSelection();
            StateChanged?.Invoke();
            Render();
        }
    }

    private void ShowContextMenu()
    {
        // 菜单已开时忽略重复右键。
        if (_menuOpen) return;
        // 重新开菜单：上一次残留的关闭按下记录不应影响这一次的点击。
        _menuClosePressPoint = null;
        GetCursorPos(out var screen);
        var rows = new List<FloatMenuRow>
        {
            new(FloatMenuRowKind.GroupLabel, text: "显示"),
            new(FloatMenuRowKind.Command, ID_BG_OPACITY, "背景不透明度",
                suffix: ToPercent(BackgroundOpacity), accentSuffix: true),
            new(FloatMenuRowKind.Command, ID_BORDER_OPACITY, "边框不透明度",
                suffix: ToPercent(BorderOpacity), accentSuffix: true),
            new(FloatMenuRowKind.Command, ID_COLORS, "阅读配色…"),
            new(FloatMenuRowKind.Command, ID_FONT_SIZE, "正文字号…"),
            new(FloatMenuRowKind.GroupLabel, text: "窗口"),
            new(FloatMenuRowKind.Command, ID_TOPMOST, "置顶", @checked: Topmost),
            new(FloatMenuRowKind.Command, ID_NORMAL, "返回普通模式", suffix: "Enter"),
            new(FloatMenuRowKind.Separator),
            new(FloatMenuRowKind.Command, ID_EXIT, "退出", danger: true),
        };

        // 深色自绘浮层（UI-11+UI-12）：先按估算尺寸在物理像素里夹回工作区，
        // 用完整包含判定（部分相交不算可见，审查缺陷 #6）；菜单实际渲染后还会按
        // 真实尺寸校正一次（见 FloatMenuWindow）。
        double scale = GetDpiForWindow(_hwnd) / 96.0;
        int estW = (int)Math.Round(FloatMenuWindow.EstimatedWidth * scale);
        int estH = (int)Math.Round(FloatMenuWindow.EstimateHeight(rows) * scale);
        int shadow = (int)Math.Round(16 * scale);
        int x = screen.X - shadow, y = screen.Y - shadow;
        var desired = new WindowPlacement.RECT { Left = x, Top = y, Right = x + estW, Bottom = y + estH };
        if (WindowPlacement.TryGetWorkAreaForRect(desired, out var workArea)
            && !WindowPlacement.IsRectContainedInWorkArea(desired, workArea))
        {
            var clamped = WindowPlacement.ClampIntoWorkArea(desired, workArea);
            x = clamped.Left;
            y = clamped.Top;
        }

        var menu = new FloatMenuWindow(rows, scale, Topmost, _hwnd, x / scale, y / scale);
        _menu = menu;
        _menuOpen = true;
        menu.ItemActivated += HandleCommand;
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            _menu = null;
            // 只有「点击浮窗本体」造成的失活才记录致关闭的那次左键按下：它会在激活转移后
            // 派发到浮窗，需吞掉以免误翻页。Esc/命令关闭、Alt+Tab 或点击其他应用都没有
            // 落到浮窗的按下，不记录——返回浮窗后的第一次独立点击立即生效（审查缺陷 #5）。
            _menuClosePressPoint = menu.ClosedByDeactivation ? CaptureMenuClosingPress() : null;
            Diagnostics.Log($"float menu closed: deactivation={menu.ClosedByDeactivation} closingPress={(_menuClosePressPoint is null ? "none" : "on-window")}");
            // 菜单命令执行后焦点返回浮窗，保证 Esc/方向键继续生效；
            // 用户切去其他应用导致的失活关闭不抢焦点。
            if (!menu.ClosedByDeactivation) FocusWindow();
        };
        menu.Show();
        menu.FocusFirstItem();
    }

    /// <summary>
    /// 菜单失活关闭的这一刻，判定是否真有「落在浮窗内的左键按下」造成它：
    /// 光标在浮窗矩形内，且这次失活把前台交给了浮窗（或左键仍按下）。
    /// 点击其他应用或 Alt+Tab 时前台在别处、光标也不在浮窗内，返回 null，后续点击不被吞。
    /// </summary>
    private POINT? CaptureMenuClosingPress()
    {
        // GetKeyState 反映线程最近取到的消息的按键状态；在 WM_LBUTTONDOWN 处理栈内两者都为按下。
        bool keyState = (GetKeyState(VkLeftButton) & 0x8000) != 0;
        bool asyncState = (GetAsyncKeyState(VkLeftButton) & 0x8000) != 0;
        // 失活先于按下派发时键状态已回到抬起（自动化点击尤其明显），此时前台已切到浮窗，
        // 仍可判定是这次按下造成的关闭；切换其他应用的前台不是浮窗。
        bool activatedFloat = GetForegroundWindow() == _hwnd;
        if (!GetCursorPos(out var point)) return null;
        if (!GetWindowRect(_hwnd, out var rect)) return null;
        bool inside = point.X >= rect.Left && point.X < rect.Right && point.Y >= rect.Top && point.Y < rect.Bottom;
        Diagnostics.Log($"menu closing press probe: keyState={keyState} asyncState={asyncState} activatedFloat={activatedFloat} cursor=({point.X},{point.Y}) rect={rect.Left},{rect.Top}..{rect.Right},{rect.Bottom}");
        if (!inside) return null;
        if (!keyState && !asyncState && !activatedFloat) return null;
        return point;
    }

    private void HandleCommand(int command)
    {
        Diagnostics.Log("float menu command=" + command);
        switch (command)
        {
            case ID_TOPMOST:
                SetTopmost(!Topmost);
                return;
            case ID_BG_OPACITY:
                OpacityPanelRequested?.Invoke(true);
                return;
            case ID_BORDER_OPACITY:
                OpacityPanelRequested?.Invoke(false);
                return;
            case ID_COLORS:
                ReadingColorsRequested?.Invoke();
                return;
            case ID_FONT_SIZE:
                ReadingFontSizeRequested?.Invoke();
                return;
            case ID_NORMAL:
                ReturnToNormalRequested?.Invoke();
                return;
            case ID_EXIT:
                ExitRequested?.Invoke();
                return;
        }
        StateChanged?.Invoke();
        Render();
    }

    /// <summary>0-1 不透明度显示为整数百分比（菜单行尾实时数值）。</summary>
    private static string ToPercent(double value) => $"{(int)Math.Round(Math.Clamp(value, 0, 1) * 100)}%";

    /// <summary>面板草稿实时预览：只改渲染，不写确认值、不触发状态保存。</summary>
    public void PreviewOpacity(double background, double border)
    {
        _opacityDraft = (Math.Clamp(background, 0, 1), Math.Clamp(border, 0, 1));
        Render();
    }

    /// <summary>「确定」：草稿值转确认值并触发状态保存（沿用 Library.Save 原子写入）。</summary>
    public void CommitOpacity(double background, double border)
    {
        _opacityDraft = null;
        BackgroundOpacity = Math.Clamp(background, 0, 1);
        BorderOpacity = Math.Clamp(border, 0, 1);
        Diagnostics.Log($"opacity bg={BackgroundOpacity} border={BorderOpacity} topmost={Topmost}");
        StateChanged?.Invoke();
        Render();
    }

    /// <summary>「取消」或面板被外部关闭：丢弃草稿，恢复确认值渲染。</summary>
    public void CancelOpacityPreview()
    {
        if (_opacityDraft is null) return;
        _opacityDraft = null;
        Render();
    }

    /// <summary>菜单命令或面板结束后把焦点交回浮窗，保证 Esc/方向键继续生效（UI-SPEC UI-12）。</summary>
    public void FocusWindow()
    {
        SetForegroundWindow(_hwnd);
        SetFocus(_hwnd);
    }

    private void CopySelection()
    {
        if (_selectionAnchor < 0 || _selectionFocus < 0) return;
        int start = Math.Min(_selectionAnchor, _selectionFocus);
        int end = Math.Max(_selectionAnchor, _selectionFocus);
        if (end <= start) return;
        try { Clipboard.SetText(_session.Text[start..end]); } catch { }
    }

    /// <summary>翻页或换书后清除旧选区，避免残留错误高亮。</summary>
    private void ClearSelection()
    {
        if (_selectionAnchor < 0 && _selectionFocus < 0) return;
        _selectionAnchor = -1;
        _selectionFocus = -1;
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
        bool sizeChanged = layoutKeyW != _layoutWidth || layoutKeyH != _layoutHeight;
        if (_page is null || sizeChanged || _session.Anchor != _laidAnchor)
        {
            if (sizeChanged)
            {
                // 重排后保持阅读位置：定位回包含当前锚点文字的页首。
                _session.JumpToOffset(_session.Anchor, dipWidth, dipHeight, scale);
            }
            // 尺寸不变但锚点已前进/后退（有效翻页或滚动）时也必须重排，
            // 否则提交的仍是旧页画面。
            _page = Paginator.Layout(_session.Text, _session.Anchor, dipWidth, dipHeight, _session.Settings, scale);
            _laidAnchor = _session.Anchor;
            _layoutWidth = layoutKeyW;
            _layoutHeight = layoutKeyH;
        }

        _presenter.EnsureSize(width, height);
        _presenter.SetDpiScale(scale);

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

        // 正文底色取应用级阅读配色（需求第 7 节）：RGB 来自所选背景色，Alpha 只由背景不透明度决定，
        // 因此 100% 为所选颜色、50% 与后方画面混合、0% 完全透出；文字与边框的不透明度各自独立。
        // 面板草稿期间渲染用草稿值，确认值不动（其他状态保存仍写确认值）。
        double bgOpacity = _opacityDraft?.Background ?? BackgroundOpacity;
        double borderOpacity = _opacityDraft?.Border ?? BorderOpacity;
        Color backgroundColor = ReadingColors.Background;
        var background = Color.FromArgb((byte)Math.Round(bgOpacity * 255),
            backgroundColor.R, backgroundColor.G, backgroundColor.B);
        if (background.A > 0)
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
        }

        HighlightRange? selection = null;
        if (_selectionAnchor >= 0 && _selectionFocus >= 0 && _selectionAnchor != _selectionFocus)
        {
            selection = new HighlightRange(Math.Min(_selectionAnchor, _selectionFocus), Math.Abs(_selectionFocus - _selectionAnchor));
        }

        PageRenderer.DrawPage(dc, page, _textBrush, CollectPageHighlights(page), selection, _highlights);

        if (borderOpacity > 0.001)
        {
            var pen = new Pen(new SolidColorBrush(Color.FromArgb((byte)Math.Round(borderOpacity * 255), 0x8A, 0x8A, 0x99)), 1.5);
            pen.Freeze();
            double inset = 1.5;
            dc.DrawRectangle(null, pen, new Rect(inset, inset, Math.Max(0, width - inset * 2), Math.Max(0, height - inset * 2)));
        }
    }

    /// <summary>当前页内的搜索命中区间：当前聚焦命中与普通命中区分样式，与普通窗口一致。</summary>
    private IReadOnlyList<HighlightRange> CollectPageHighlights(PageLayout page)
    {
        if (_session.SearchHits.Count == 0) return Array.Empty<HighlightRange>();
        var ranges = new List<HighlightRange>();
        int pageEnd = page.End;
        for (int i = 0; i < _session.SearchHits.Count; i++)
        {
            int hit = _session.SearchHits[i];
            if (hit + _session.SearchHitLength <= page.Start) continue;
            if (hit >= pageEnd) break;
            var kind = i == _session.ActiveSearchHit ? HighlightKind.Current : HighlightKind.Match;
            ranges.Add(new HighlightRange(hit, _session.SearchHitLength, kind));
        }
        return ranges;
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

    /// <summary>导出当前浮窗状态（保存用），坐标尺寸为 DIP。</summary>
    public FloatState CaptureState()
    {
        GetWindowRect(_hwnd, out var rect);
        double scale = Scale;
        return new FloatState
        {
            BackgroundOpacity = BackgroundOpacity,
            BorderOpacity = BorderOpacity,
            Topmost = Topmost,
            X = rect.Left / scale,
            Y = rect.Top / scale,
            Width = (rect.Right - rect.Left) / scale,
            Height = (rect.Bottom - rect.Top) / scale,
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // 静态事件：浮窗销毁后必须摘掉，否则切换书籍后旧窗口仍被引用并继续重绘。
        ReadingColors.Changed -= OnColorsChanged;
        _presenter.Dispose();
        if (_hwnd != IntPtr.Zero)
        {
            lock (Instances) Instances.Remove(_hwnd);
            DestroyWindow(_hwnd);
        }
    }
}
