using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using HiddenReader.App.Data;
using HiddenReader.App.Float;
using HiddenReader.Core.Reading;

namespace HiddenReader.App.UI;

public partial class MainWindow : Window
{
    private readonly Library _library = new();
    private readonly BackgroundPager _pager;
    private ReadingSession? _session;
    private BookInfo? _currentBook;
    private List<ChapterInfo> _chapters = new();
    private FloatWindow? _float;
    private int _readingFontSize;
    private ReadingFontSizeWindow? _fontSizePanel;
    /// <summary>导入/打开期间为真：状态栏显示阶段文字，期间不刷新页码。</summary>
    private bool _busy;
    /// <summary>开书序号：切换书籍或重复选择时使旧的异步结果作废。</summary>
    private int _openGeneration;
    /// <summary>搜索序号：Esc、改查询或换书后使仍在后台执行的旧搜索结果作废（不回填）。</summary>
    private int _searchGeneration;
    /// <summary>进行中的后台扫描的取消源：Esc、改查询、换书时真正取消扫描（审查缺陷 #1）。</summary>
    private CancellationTokenSource? _searchCts;
    /// <summary>取消进行中的扫描（若有）。作废结果 + 停止后台执行。</summary>
    private void CancelPendingSearch()
    {
        _searchCts?.Cancel();
        _searchCts?.Dispose();
        _searchCts = null;
    }
    /// <summary>最近一次已执行（或在执行）的搜索查询：停顿计数的去重依据。</summary>
    private string _lastSearchQuery = "\uFFFF";
    /// <summary>输入停顿 ~300ms 后触发即时计数（UI-06）。</summary>
    private readonly DispatcherTimer _searchDebounce;

    public MainWindow()
    {
        InitializeComponent();
        // 应用级阅读配色先于任何绘制载入：旧状态缺少颜色字段时使用默认值。
        ReadingColors.Load(_library.State);
        _readingFontSize = _library.State.ReadingFontSize;
        // 标题栏在句柄就绪后由 DWM 着色：系统浅色主题下也保持深色，并随焦点调整文字亮度。
        // 顶部栏改为客户区自绘后，DWM 标题栏不再可见；保留着色使窗口边缘细节保持深色。
        DarkTitleBar.Attach(this);
        // 分页结果回调统一封送回 UI 线程。
        _pager = new BackgroundPager(result => Dispatcher.BeginInvoke(() => ApplyPageStarts(result)));
        // 搜索即时计数：输入停顿约 300ms 后在后台统计（UI-06），回车仍走跳转入口。
        _searchDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(300),
        };
        _searchDebounce.Tick += OnSearchDebounceTick;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var state = _library.State;
        Width = state.WindowWidth;
        Height = state.WindowHeight;
        if (WindowPlacement.IsUnplaced(state.WindowX, state.WindowY))
        {
            // 首次启动：贴工作区右侧、偏下放置。
            var work = SystemParameters.WorkArea;
            Left = Math.Max(8, work.Right - Width - 16);
            Top = Math.Max(8, work.Top + (work.Height - Height) / 2 + 40);
        }
        else
        {
            // 有效坐标可以为负：左侧/上方显示器的窗口本来就带负坐标。
            Left = state.WindowX!.Value;
            Top = state.WindowY!.Value;
        }

        Reader.SessionAdvanced += OnReadingAdvanced;
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += OnWindowClosing;
        // 放置完成后再挂接状态保存，避免初始化期间把首次定位标志覆盖掉。
        StateChanged += OnStateChanged;
        SearchBox.TextChanged += (_, _) =>
        {
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            // 查询已变化：立即取消仍在后台执行的旧扫描（审查缺陷 #1），
            // 旧结果作废不回填；同时重启停顿计数计时器（UI-06）。
            CancelPendingSearch();
            _searchGeneration++;
            _searchDebounce.Stop();
            _searchDebounce.Start();
        };
        // 首次定位立即落盘（此后由 StateChanged 增量保存）。
        SaveWindowState();
        // 在物理像素坐标系里核对位置是否仍落在可见工作区
        // （显示器断开或坐标越界时回退，避免窗口恢复到屏幕外）。
        WindowPlacement.EnsureWindowVisible(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);

        Reader.LayoutSizeChanged += OnReaderLayoutChanged;
        AttachMenuKeyboardNav();
        UpdateTopbarOpenStates();
        RefreshShelf();
        var last = _library.State.Books.FirstOrDefault(b => b.Id == state.LastBookId);
        if (last is not null)
        {
            _ = OpenBookAsync(last);
        }
        else
        {
            // 空书架：状态栏给出导入入口提示。
            UpdateStatusLabel();
        }
    }

    // ---------- 顶部栏与窗口控制 ----------

    private void OnStateChanged(object? sender, EventArgs e)
    {
        // 最大化时窗口边框超出屏幕一个缩放边宽，内边距补回避免内容贴出可视区。
        bool maximized = WindowState == WindowState.Maximized;
        RootBorder.Margin = maximized ? new Thickness(7) : default;
        MaxGlyph.Visibility = maximized ? Visibility.Collapsed : Visibility.Visible;
        RestoreGlyph.Visibility = maximized ? Visibility.Visible : Visibility.Collapsed;
        SaveWindowState();
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    // ---------- 顶部菜单弹层 ----------

    private void OnFileMenuClick(object sender, RoutedEventArgs e) => ToggleMenu(FileMenuPopup);

    private void OnViewMenuClick(object sender, RoutedEventArgs e) => ToggleMenu(ViewMenuPopup);

    private void OnSettingsMenuClick(object sender, RoutedEventArgs e) => ToggleMenu(SettingsMenuPopup);

    private void OnHelpMenuClick(object sender, RoutedEventArgs e) => ToggleMenu(HelpMenuPopup);

    /// <summary>
    /// 弹层 StaysOpen=True，IsOpen 不会在 Click 到达前被外击自动改写，
    /// 这里读到的是精确开关状态：开→收起（同图标再点即关闭），关→互斥展开。
    /// 不依赖 Popup.Closed（实测迟到约 150ms，无法区分「本次按下关闭」与「早前关闭」，D1 根因）。
    /// 展开时焦点默认落在第一条命令，支持 ↑↓/Enter/Esc 键盘导航（UI-05）。
    /// </summary>
    private void ToggleMenu(Popup popup)
    {
        var wasOpen = popup.IsOpen;
        CloseMenus();
        popup.IsOpen = !wasOpen;
        if (!popup.IsOpen) return;
        if (ReferenceEquals(popup, FileMenuPopup))
        {
            // 列表上限 360 且不超过工作区高度 50%（UI-SPEC 2.2）；打开时启动进度后台填充。
            ShelfScroll.MaxHeight = Math.Min(360, SystemParameters.WorkArea.Height * 0.5);
            BeginShelfProgressFill();
        }
        FocusFirstCommand(popup);
    }

    /// <summary>点在触发图标或菜单内时保留弹层，让对应的 Click 先处理。</summary>
    private void OnWindowPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src && IsWithinMenu(src)) return;
        CloseMenus();
    }

    private bool IsWithinMenu(DependencyObject src)
    {
        for (DependencyObject? d = src; d != null; d = (d is System.Windows.Media.Visual v) ? VisualTreeHelper.GetParent(v) : null)
        {
            if (ReferenceEquals(d, BookButton) || ReferenceEquals(d, EyeButton)
                || ReferenceEquals(d, GearButton) || ReferenceEquals(d, HelpButton)
                || ReferenceEquals(d, FileMenuPopup.Child) || ReferenceEquals(d, ViewMenuPopup.Child)
                || ReferenceEquals(d, SettingsMenuPopup.Child) || ReferenceEquals(d, HelpMenuPopup.Child)) return true;
        }
        return false;
    }

    /// <summary>窗口失活（切走、弹出系统对话框）时收起弹层，模拟 StaysOpen=False 的外击收起语义。</summary>
    private void OnWindowDeactivated(object? sender, EventArgs e) => CloseMenus();

    /// <summary>标题栏拖动、边缘缩放会移动窗口：与旧行为一致，窗口移动即收起弹层。</summary>
    private void OnWindowLocationChanged(object? sender, EventArgs e) => CloseMenus();

    // ---------- 顶栏激活态与弹层键盘导航（UI-05） ----------

    private static readonly SolidColorBrush TopbarOpenBrush = FreezeBrush(Color.FromRgb(0x26, 0x39, 0x54));
    private static readonly SolidColorBrush IconIdleBrush = FreezeBrush(Color.FromRgb(0xAF, 0xC5, 0xE2));
    private static readonly SolidColorBrush IconOpenBrush = FreezeBrush(Color.FromRgb(0xD8, 0xE8, 0xFF));

    private static SolidColorBrush FreezeBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    /// <summary>弹层展开期间触发按钮呈打开态（state.selected 底 + 提亮描边），关闭后还原（UI-SPEC 2.1）。</summary>
    private void UpdateTopbarOpenStates()
    {
        SetTopbarOpen(BookButton, FileMenuPopup.IsOpen, glyph: null);
        SetTopbarOpen(EyeButton, ViewMenuPopup.IsOpen, EyeGlyph);
        SetTopbarOpen(GearButton, SettingsMenuPopup.IsOpen, GearGlyph);
        SetTopbarOpen(HelpButton, HelpMenuPopup.IsOpen, HelpGlyph);
        SetTopbarOpen(FloatButton, open: false, FloatGlyph); // 浮窗按钮无弹层，恒为闲置态
    }

    private void SetTopbarOpen(Button button, bool open, System.Windows.Shapes.Path? glyph)
    {
        // 书架按钮内层 23×23 蓝底方块不变，只改外层触发按钮底色。
        button.Background = open ? TopbarOpenBrush : Brushes.Transparent;
        if (glyph is not null)
        {
            glyph.Stroke = open ? IconOpenBrush : IconIdleBrush;
        }
    }

    /// <summary>把弹层的开合与键盘导航接上：开合刷新顶栏激活态；弹层内的按键属于菜单交互。</summary>
    private void AttachMenuKeyboardNav()
    {
        HookMenuPopup(FileMenuPopup, BookButton);
        HookMenuPopup(ViewMenuPopup, EyeButton);
        HookMenuPopup(SettingsMenuPopup, GearButton);
        HookMenuPopup(HelpMenuPopup, HelpButton);
    }

    private void HookMenuPopup(Popup popup, Button trigger)
    {
        popup.Opened += (_, _) => UpdateTopbarOpenStates();
        popup.Closed += (_, _) => UpdateTopbarOpenStates();
        // 弹层有自己的 hwnd：焦点进入弹层后按键只经过 Child 的隧道路由，
        // 不再经过 MainWindow.PreviewKeyDown，因此 ↑↓/Enter/Esc 必须挂在 Child 上。
        if (popup.Child is FrameworkElement child)
        {
            child.PreviewKeyDown += (_, e) => OnMenuChildKeyDown(popup, trigger, e);
        }
    }

    private void OnMenuChildKeyDown(Popup popup, Button trigger, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up:
            case Key.Down:
                MoveMenuFocus(popup, e.Key == Key.Up ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (Keyboard.FocusedElement is Button button)
                {
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                }
                e.Handled = true;
                break;
            case Key.Escape:
                CloseTopOverlay();
                e.Handled = true;
                break;
        }
    }

    /// <summary>按文档顺序收集弹层内可用的命令按钮（书架行按钮也参与键盘导航）。</summary>
    private static List<Button> CollectCommandButtons(DependencyObject root)
    {
        var buttons = new List<Button>();
        Collect(root, buttons);
        return buttons;

        static void Collect(DependencyObject node, List<Button> acc)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < count; i++)
            {
                if (VisualTreeHelper.GetChild(node, i) is not DependencyObject child) continue;
                if (child is Button b && b.IsVisible && b.IsEnabled && b.Focusable)
                {
                    acc.Add(b);
                }
                else
                {
                    Collect(child, acc);
                }
            }
        }
    }

    private void FocusFirstCommand(Popup popup)
    {
        if (popup.Child is null) return;
        // 弹层子树首次展开时异步挂入视觉树，Input 优先级确保在布局后取焦点。
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var buttons = CollectCommandButtons(popup.Child);
            if (buttons.Count > 0) buttons[0].Focus();
        }));
    }

    private void MoveMenuFocus(Popup popup, int delta)
    {
        if (popup.Child is null) return;
        var buttons = CollectCommandButtons(popup.Child);
        if (buttons.Count == 0) return;
        int index = buttons.FindIndex(b => ReferenceEquals(Keyboard.FocusedElement, b));
        if (index < 0)
        {
            index = delta > 0 ? 0 : buttons.Count - 1;
        }
        else
        {
            index = (index + delta + buttons.Count) % buttons.Count;
        }
        buttons[index].Focus();
    }

    /// <summary>收起全部菜单弹层；不触碰焦点归属（由各入口自行决定）。</summary>
    private void CloseMenus()
    {
        FileMenuPopup.IsOpen = false;
        ViewMenuPopup.IsOpen = false;
        SettingsMenuPopup.IsOpen = false;
        HelpMenuPopup.IsOpen = false;
    }

    // ---------- 帮助窗口 ----------

    private void OnOpenGuide(object sender, RoutedEventArgs e) => ShowHelp(HelpWindow.CreateGuide());

    private void OnOpenShortcuts(object sender, RoutedEventArgs e) => ShowHelp(HelpWindow.CreateShortcuts());

    /// <summary>
    /// 在应用内打开帮助窗口（模态）。关闭后焦点回到问号图标，便于键盘继续操作。
    /// 帮助窗口是独立 Window，其中的按键不会进入正文翻页路径。
    /// </summary>
    private void ShowHelp(HelpWindow window)
    {
        CloseMenus();
        window.Owner = this;
        window.ShowDialog();
        HelpButton.Focus();
    }

    // ---------- 书架 ----------

    /// <summary>书架行模型：进度百分比由后台计算补齐（UI-10），未就绪时为 null（下行留空占位）。</summary>
    private sealed class ShelfItem : System.ComponentModel.INotifyPropertyChanged
    {
        public ShelfItem(BookInfo book, bool isCurrent, string? progress)
        {
            Book = book;
            IsCurrent = isCurrent;
            _progress = progress;
        }

        public BookInfo Book { get; }
        public bool IsCurrent { get; }
        private string? _progress;

        public string? ProgressText
        {
            get => _progress;
            set
            {
                if (_progress == value) return;
                _progress = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(ProgressText)));
            }
        }

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>会话内进度缓存：bookId → (解码键, 正文 UTF-16 长度)。编码变更后键不匹配自动失效。</summary>
    private readonly Dictionary<string, (string? Encoding, int Length)> _progressCache = new();
    /// <summary>进度填充序号：弹层关闭或书架刷新后使旧的后台填充任务作废。</summary>
    private int _progressGeneration;
    private List<ShelfItem> _shelfRows = new();

    private void RefreshShelf()
    {
        var books = _library.State.Books;
        _shelfRows = books.Select(b => new ShelfItem(b, b.Id == _currentBook?.Id, GetShelfProgressText(b))).ToList();
        ShelfList.ItemsSource = _shelfRows;
        ShelfCountText.Text = books.Count + " 本";
        ShelfEmptyText.Visibility = books.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (FileMenuPopup.IsOpen) BeginShelfProgressFill();
    }

    /// <summary>书架弹层打开时启动后台进度填充（UI-SPEC 2.2）：未就绪的行先只显书名，就绪后补百分比。</summary>
    private void BeginShelfProgressFill()
    {
        int generation = ++_progressGeneration;
        // UI 线程取快照：编码与锚点，后台只做解码与计数，不触碰书库状态。
        var snapshot = _shelfRows
            .Where(row => !_progressCache.ContainsKey(row.Book.Id)
                || _progressCache[row.Book.Id].Encoding != _library.GetBookState(row.Book.Id).Encoding)
            .Select(row =>
            {
                var state = _library.State.PerBook.TryGetValue(row.Book.Id, out var s) ? s : null;
                return (Book: row.Book, Encoding: state?.Encoding, Anchor: state?.Anchor ?? 0);
            })
            .ToList();
        if (snapshot.Count == 0) return;

        Task.Run(() =>
        {
            foreach (var job in snapshot)
            {
                if (generation != _progressGeneration) return;
                try
                {
                    // 顺序解码：20 本大书不并发冲击磁盘；长度缓存后重开弹层零成本。
                    string path = Path.Combine(_library.BooksDir, job.Book.FileName);
                    byte[] bytes = File.ReadAllBytes(path);
                    string text = Library.DecodeCopy(bytes, job.Encoding);
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (generation != _progressGeneration) return;
                        _progressCache[job.Book.Id] = (job.Encoding, text.Length);
                        UpdateShelfProgressTexts();
                    });
                }
                catch (Exception ex)
                {
                    Diagnostics.Log("shelf progress failed for '" + job.Book.Title + "': " + ex.Message);
                }
            }
        });
    }

    /// <summary>按最新锚点回填各行进度文本：当前书用会话锚点，其余用书库保存值。</summary>
    private void UpdateShelfProgressTexts()
    {
        foreach (var row in _shelfRows)
        {
            row.ProgressText = GetShelfProgressText(row.Book);
        }
    }

    private string? GetShelfProgressText(BookInfo book)
    {
        var state = _library.State.PerBook.TryGetValue(book.Id, out var s) ? s : null;
        string? encodingKey = state?.Encoding;
        if (!_progressCache.TryGetValue(book.Id, out var cache) || cache.Encoding != encodingKey) return null;
        int anchor = book.Id == _currentBook?.Id && _session is not null ? _session.Anchor : state?.Anchor ?? 0;
        int length = cache.Length;
        double pct = length > 0 ? Math.Clamp(anchor, 0, length) / (double)length : 0;
        return $"{(int)Math.Round(pct * 100)}%";
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
        CloseMenus();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 TXT 小说",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        // 复制副本在后台线程执行：期间窗口可重绘、可移动，并显示当前阶段。
        ImportButton.IsEnabled = false;
        _busy = true;
        PageLabel.Text = $"正在导入《{Path.GetFileNameWithoutExtension(dialog.FileName)}》…";
        SetPageNavEnabled(false);
        try
        {
            // 副本复制在后台线程执行，避免大文件/慢磁盘冻结 UI（技术方案 §39）。
            var book = await Task.Run(() => _library.Import(dialog.FileName));
            if (_busy)
            {
                RefreshShelf();
                await OpenBookAsync(book);
            }
        }
        catch (Exception ex)
        {
            _busy = false;
            PageLabel.Text = string.Empty;
            MessageBox.Show(this, "导入失败：" + ex.Message, "HiddenReader");
        }
        finally
        {
            ImportButton.IsEnabled = true;
        }
    }

    private void OnShelfBookClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShelfItem item) return;
        CloseMenus();
        _ = OpenBookAsync(item.Book);
    }

    private void OnShelfRemoveClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not BookInfo book) return;
        if (MessageBox.Show(this, $"移除《{book.Title}》？\n仅删除软件管理的副本，原文件不受影响。", "HiddenReader",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            KeepFileMenuOpen();
            return;
        }

        bool removed = _library.Remove(book);
        if (!removed)
        {
            // 书架提交失败（Library.Remove 已回滚内存状态）：书架条目、当前正文、
            // 阅读位置和浮窗全部保持原样，只提示，不刷新列表（避免当前书被重新打开）。
            MessageBox.Show(this, "书架保存失败，移除已取消。\n当前书籍与阅读位置未受影响，请检查磁盘后重试。",
                "HiddenReader", MessageBoxButton.OK, MessageBoxImage.Warning);
            KeepFileMenuOpen();
            return;
        }

        if (_currentBook?.Id == book.Id)
        {
            // 提交成功（Remove 返回 true；副本删除失败仅记日志、由启动清理兜底）才走清理路径。
            _openGeneration++; // 使仍在进行的开书结果作废，避免恢复已被移除的书。
            CloseReadingColorsPanel();
            CloseOpacityPanel();
            _float?.Dispose();
            _float = null;
            _currentBook = null;
            _session = null;
            Reader.SetSession(null);
            PageLabel.Text = string.Empty;
            SearchStatus.Text = string.Empty;
            RefreshShelf();
            // 后继书选择规则沿用 Library.Remove：LastBookId 已指向书架末位（或 null）。
            var next = _library.State.Books.FirstOrDefault(b => b.Id == _library.State.LastBookId);
            if (next is not null)
            {
                _ = OpenBookAsync(next);
            }
            else
            {
                // 空书架：清掉旧书残留的目录面板与章节列表，页码/翻页按钮由
                // UpdateStatusLabel 的无会话分支统一重置（含「从文件导入」提示）。
                _chapters = new List<ChapterInfo>();
                TocPanel.Visibility = Visibility.Collapsed;
                TocCheck.Visibility = Visibility.Collapsed;
                if (FindBarRow.Visibility == Visibility.Visible) CloseFindbar();
                UpdateStatusLabel();
            }
        }
        else
        {
            // 移除非当前书：当前正文、进度与浮窗保持原样，只刷新书架。
            RefreshShelf();
        }
        KeepFileMenuOpen();
    }

    /// <summary>移除确认框抢走激活后弹层可能已收起：重新展开文件菜单，方便继续操作。</summary>
    private void KeepFileMenuOpen()
    {
        ViewMenuPopup.IsOpen = false;
        SettingsMenuPopup.IsOpen = false;
        HelpMenuPopup.IsOpen = false;
        FileMenuPopup.IsOpen = true;
    }

    // ---------- 编码切换（UI-04） ----------

    /// <summary>
    /// 书架条目右键「编码…」：切换后按旧/新解码正文的 UTF-16 长度比例重映射锚点，
    /// 编码与新锚点一并原子保存；只读书库副本，原文件不受影响。
    /// 保存成功且目标书仍是当前书、会话未被替换时才重新打开；任何交错的切书/移除
    /// 操作优先：目标书已被后续开书重新打开时放弃本次切换（运行会话按旧编码解码，
    /// 写入新编码会与当前正文和后续保存的锚点错位），目标书不在前台时只写该书状态
    /// （审查缺陷 #3）。
    /// </summary>
    private async void OnShelfEncodingClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not ShelfItem item) return;
        if (_busy) return; // 导入/开书进行中：避免与开书流程竞争同一本书
        var book = item.Book;
        var state = _library.GetBookState(book.Id);
        string? current = state.Encoding;
        var dialog = new EncodingWindow(book.Title, current) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        string? encoding = dialog.SelectedEncoding;
        if (encoding == current) return; // 未变化：不重解码

        try
        {
            // UI 线程快照：会话引用与开书代次。字符串不可变，引用快照即内容快照。
            int openGenerationAtStart = _openGeneration;
            var sessionAtStart = _session;
            byte[] bytes = await Task.Run(() =>
            {
                // 可控时序测试钩子（复验用）：拉长解码窗口以复现与切书的交错。
                if (int.TryParse(Environment.GetEnvironmentVariable("HR_ENCODING_DELAY_MS"), out int delay) && delay > 0)
                {
                    Thread.Sleep(delay);
                }
                return File.ReadAllBytes(Path.Combine(_library.BooksDir, book.FileName));
            });

            // await 之后在 UI 线程重新判定目标书是否仍是当前书且会话未被替换：
            // await 期间用户可能已切到书 B、移除该书或重新打开，旧判定不可沿用。
            bool sameSession = _openGeneration == openGenerationAtStart
                && book.Id == _currentBook?.Id
                && ReferenceEquals(_session, sessionAtStart);
            int oldAnchor;
            string oldText;
            if (sameSession && _session is not null)
            {
                // 当前书：旧锚点取此刻会话值（含切换期间用户在本书上的翻页），旧解码用会话文本。
                oldAnchor = _session.Anchor;
                oldText = _session.Text;
            }
            else
            {
                // 非当前书：PerBook.Anchor 不会被其他阅读操作推进，直接读取并重解码旧文。
                oldAnchor = state.Anchor;
                oldText = await Task.Run(() => Library.DecodeCopy(bytes, current));
            }
            string newText = await Task.Run(() => Library.DecodeCopy(bytes, encoding));
            int newAnchor = Library.RemapAnchor(oldAnchor, oldText.Length, newText.Length);

            // 提交前再核对一次（第二次 await 期间用户可能又切书/移除）。
            if (!_library.State.Books.Contains(book))
            {
                Diagnostics.Log($"encoding '{book.Title}' abandoned: removed during switch");
                return;
            }
            bool targetIsCurrent = book.Id == _currentBook?.Id;
            bool stillSameSession = targetIsCurrent
                && _openGeneration == openGenerationAtStart
                && ReferenceEquals(_session, sessionAtStart);
            if (targetIsCurrent && !stillSameSession)
            {
                // 目标书已被后续开书重新打开（如 A→B→A 在解码完成前返回 A）：运行会话仍是
                // 旧编码解出的正文，此时写入新编码会让持久化编码、当前正文与后续
                // SaveProgress 落盘的锚点分属两套 UTF-16 偏移（审查缺陷 #3）。
                // 放弃这次已被取代的切换：不写编码、不改锚点，运行会话与落盘保持一致。
                Diagnostics.Log($"encoding '{book.Title}' -> {encoding ?? "auto"} abandoned: superseded by a later open, live encoding stays {current ?? "auto"}");
                return;
            }

            string? previousEncoding = state.Encoding;
            int previousAnchor = state.Anchor;
            state.Encoding = encoding; // null = 回到自动识别
            state.Anchor = newAnchor;
            if (!_library.Save())
            {
                state.Encoding = previousEncoding;
                state.Anchor = previousAnchor;
                MessageBox.Show(this, "书架保存失败，编码切换已取消。\n当前书籍与阅读位置未受影响，请检查磁盘后重试。",
                    "HiddenReader", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Diagnostics.Log($"encoding '{book.Title}' -> {encoding ?? "auto"} anchor {oldAnchor}->{newAnchor} reopen={stillSameSession}");
            if (stillSameSession)
            {
                // 当前书重新打开：ReadBook 按 BookState.Encoding 解码，锚点已重映射。
                await OpenBookAsync(book);
            }
            RefreshShelf();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "编码切换失败：" + ex.Message, "HiddenReader");
        }
    }

    private async Task OpenBookAsync(BookInfo book)
    {
        // 换书前关掉配色面板：配色是应用级的，但面板的预览草稿不应跨书残留。
        CloseReadingColorsPanel();
        // 不透明度面板同理：宿主即将切书，草稿按「取消」处理。
        CloseOpacityPanel();
        int generation = ++_openGeneration;
        _searchGeneration++; // 换书丢弃过期搜索结果（REVIEW P0-1 验收④）
        CancelPendingSearch(); // 取消仍在后台执行的旧书扫描（审查缺陷 #1）
        _lastSearchQuery = "\uFFFF"; // 新书新查询：停顿计数不得因旧查询去重而跳过
        _searchDebounce.Stop();
        // 解码与章节识别都在后台线程：期间窗口可重绘、可移动，并显示当前阶段。
        _busy = true;
        SetPageNavEnabled(false);
        PageBox.IsEnabled = false;
        PageLabel.Text = $"正在打开《{book.Title}》：解码正文…";
        try
        {
            // 切书时旧的浮窗绑定的是上一本书的会话，直接丢弃。
            _float?.Dispose();
            _float = null;
            string text = await Task.Run(() => _library.ReadBook(book));
            if (generation != _openGeneration) return;

            // 正文解码完成即显示并可阅读：单页排版只依赖锚点，不等章节识别与全书分页。
            _currentBook = book;
            _session = new ReadingSession(text);
            _session.Settings.FontSize = _readingFontSize;
            var bookState = _library.GetBookState(book.Id);
            _session.Anchor = Math.Clamp(bookState.Anchor, 0, text.Length);
            Reader.SetSession(_session);
            Reader.Focus();
            _library.State.LastBookId = book.Id;
            _library.Save();
            RefreshShelf();

            _busy = false;
            UpdateStatusLabel();
            RequestPagination();
            // 换书后搜索栏不得滞留旧书状态：清掉旧查询、旧计数与「搜索中…」（审查缺陷 #1）。
            SearchBox.Clear();
            SearchStatus.Text = string.Empty;
            Diagnostics.Log($"search bar reset on open '{book.Title}'");

            // 启动恢复：App.OnStartup 在异步开书完成前调用过一次（当时还没有会话），
            // 会话就绪后在这里补齐浮窗恢复，不必等章节识别。
            RestoreFloatModeIfRequested();

            PageLabel.Text = $"正在打开《{book.Title}》：识别章节…";
            // 章节识别只写入局部结果，代次校验通过后才提交到 _chapters：
            // 旧开书任务不得污染已切换后的新书目录数据（审查缺陷 #2）。
            var detectedChapters = await Task.Run(() => Chapters.Detect(text));
            if (generation != _openGeneration) return;
            _chapters = detectedChapters;
            Diagnostics.Log($"book '{book.Title}' chars={text.Length} chapters={_chapters.Count}");
            UpdateStatusLabel();
            // 切书后目录只接受当前书的章节结果：面板开着时立即按新书重建并重定位。
            if (TocPanel.Visibility == Visibility.Visible)
            {
                RebuildTocItems();
                SyncTocToCurrentChapter(scrollIntoView: true);
            }
        }
        catch (Exception ex)
        {
            if (generation != _openGeneration) return;
            _busy = false;
            PageLabel.Text = string.Empty;
            MessageBox.Show(this, "打开书籍失败：" + ex.Message, "HiddenReader");
        }
    }

    /// <summary>按当前视图尺寸请求后台全书分页；结果就绪后由 ApplyPageStarts 应用。</summary>
    private void RequestPagination()
    {
        if (_session is null || Reader.ActualWidth < 2 || Reader.ActualHeight < 2) return;
        double ppd = VisualTreeHelper.GetDpi(Reader).DpiScaleX;
        _pager.Request(_session, Reader.ActualWidth, Reader.ActualHeight, ppd);
    }

    /// <summary>
    /// 视图完成首次布局或缩放后：请求全书分页并刷新页码显示。
    /// 启动时开书早于布局完成，此处是页码/百分比首次可见的时机。
    /// </summary>
    private void OnReaderLayoutChanged()
    {
        RequestPagination();
        UpdateStatusLabel();
    }

    private void ApplyPageStarts(BackgroundPager.Result result)
    {
        // 切书、缩放或滑块预览后，过期结果不能覆盖当前字号的页码。
        if (_session is null || _session != result.Session || _session.Settings.FontSize != result.FontSize) return;
        if (Reader.ActualWidth < 2 || Reader.ActualHeight < 2) return;
        double ppd = VisualTreeHelper.GetDpi(Reader).DpiScaleX;
        if (Reader.ActualWidth != result.Width || Reader.ActualHeight != result.Height
            || Math.Abs(ppd - result.Ppd) > 0.001) return;
        _session.SetPageStarts(result.Starts, result.Width, result.Height, result.Ppd);
        UpdateStatusLabel();
    }

    private void OnReadingAdvanced()
    {
        UpdateStatusLabel();
        SaveProgress();
        SyncTocIfOpen();
    }

    private void SetPageNavEnabled(bool enabled)
    {
        PrevPageButton.IsEnabled = enabled;
        NextPageButton.IsEnabled = enabled;
    }

    /// <summary>
    /// 刷新状态栏：左侧显示章节（或导入/打开阶段文字），右侧为页码输入框、
    /// 总页数和上一页/下一页入口。总页数未算好时显示阅读百分比（锚点/正文长度），
    /// 按页跳转等分页就绪后启用。
    /// 不在此处做全书分页——翻页和滚动只做单页排版。
    /// </summary>
    private void UpdateStatusLabel()
    {
        // 导入/打开阶段由流程自己写阶段文字，这里不覆盖。
        if (_busy) return;
        if (_session is null || Reader.ActualWidth < 2)
        {
            // 空书架：状态栏给出导入入口短提示（UI-13，只描述现有导入路径）。
            PageLabel.Text = _currentBook is null ? "打开左上角书页图标导入" : string.Empty;
            PageTotal.Text = "页";
            PageBox.Clear();
            PageBox.IsEnabled = false;
            SetPageNavEnabled(false);
            return;
        }
        double ppd = VisualTreeHelper.GetDpi(Reader).DpiScaleX;
        if (!_session.HasPageStarts(Reader.ActualWidth, Reader.ActualHeight, ppd))
        {
            double fraction = _session.Text.Length > 0 ? (double)_session.Anchor / _session.Text.Length : 0;
            PageLabel.Text = $"阅读 {fraction * 100:0.#}%　总页数计算中…";
            PageTotal.Text = "页";
            PageBox.Clear();
            PageBox.IsEnabled = false;
            SetPageNavEnabled(false);
            return;
        }
        int page = _session.CachedPageIndex(_session.Anchor);
        int total = _session.CachedPageCount;
        // 与分页前分支同一锚点比例：分页完成后数值连续。
        double readyFraction = _session.Text.Length > 0 ? (double)_session.Anchor / _session.Text.Length : 0;
        string? chapter = CurrentChapterTitle();
        PageLabel.Text = chapter ?? string.Empty;
        // 百分比常驻（REVIEW P1-3）：与分页前的「阅读 x%」同一锚点比例，数值前后连续。
        PageTotal.Text = $"/ {total} 页 · {readyFraction * 100:0.#}%";
        PageBox.IsEnabled = true;
        PageBox.Text = page.ToString();
        PrevPageButton.IsEnabled = page > 1;
        NextPageButton.IsEnabled = page < total;
    }

    private string? CurrentChapterTitle()
    {
        if (_session is null) return null;
        ChapterInfo? current = null;
        foreach (var ch in _chapters)
        {
            if (ch.Offset <= _session.Anchor) current = ch;
            else break;
        }
        return current?.Title;
    }

    private void SaveProgress()
    {
        if (_currentBook is null || _session is null) return;
        _library.GetBookState(_currentBook.Id).Anchor = _session.Anchor;
        _library.Save();
    }

    private void SaveWindowState()
    {
        _library.State.WindowX = Left;
        _library.State.WindowY = Top;
        _library.State.WindowWidth = Width;
        _library.State.WindowHeight = Height;
        _library.Save();
    }

    // ---------- 键盘 ----------

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Esc：先关最上层的浮层（菜单/搜索栏），焦点落到可继续操作的位置。
        if (e.Key == Key.Escape && CloseTopOverlay())
        {
            e.Handled = true;
            return;
        }

        // 文本输入控件（搜索框、跳页框）保留系统编辑行为，Ctrl+C 不得拦截；
        // 其中的方向键等按键也不能误触发正文翻页。
        if (Keyboard.FocusedElement is TextBoxBase) return;

        // 弹层打开但焦点仍在正文/触发按钮上时：↑↓/Enter 先进入菜单导航，
        // 焦点进入弹层后由弹层 Child 的 PreviewKeyDown 接管（弹层有自己的 hwnd）。
        foreach (var popup in new[] { FileMenuPopup, ViewMenuPopup, SettingsMenuPopup, HelpMenuPopup })
        {
            if (!popup.IsOpen) continue;
            switch (e.Key)
            {
                case Key.Up:
                    MoveMenuFocus(popup, -1);
                    e.Handled = true;
                    return;
                case Key.Down:
                    MoveMenuFocus(popup, 1);
                    e.Handled = true;
                    return;
                case Key.Enter when Keyboard.FocusedElement is Button button:
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                    e.Handled = true;
                    return;
            }
            // 其余按键属于菜单交互，不进入正文翻页。
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                // Ctrl+O：普通窗口内与「文件 → 导入 TXT…」同一入口。
                case Key.O:
                    OnImport(this, e);
                    e.Handled = true;
                    return;
                // Ctrl+F：打开搜索栏并聚焦输入框。
                case Key.F:
                    if (_session is not null) OpenFindbar();
                    e.Handled = true;
                    return;
            }
        }

        // Ctrl+C：阅读视图有焦点且有选区时复制选中正文。
        if (e.Key == Key.C
            && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control
            && Reader.IsKeyboardFocusWithin)
        {
            string? selected = Reader.GetSelectedText();
            if (string.IsNullOrEmpty(selected)) return;
            try { Clipboard.SetText(selected); } catch { /* 剪贴板被占用时忽略 */ }
            e.Handled = true;
            return;
        }

        if (Reader.HandleKey(e.Key))
        {
            UpdateStatusLabel();
            SaveProgress();
            e.Handled = true;
        }
    }

    /// <summary>
    /// 关闭最上层的浮层（文件菜单、查看菜单、搜索栏）。
    /// 返回是否有关闭动作；焦点移回触发按钮或正文视图。
    /// </summary>
    private bool CloseTopOverlay()
    {
        if (FileMenuPopup.IsOpen)
        {
            FileMenuPopup.IsOpen = false;
            BookButton.Focus();
            return true;
        }
        if (ViewMenuPopup.IsOpen)
        {
            ViewMenuPopup.IsOpen = false;
            EyeButton.Focus();
            return true;
        }
        if (SettingsMenuPopup.IsOpen)
        {
            SettingsMenuPopup.IsOpen = false;
            GearButton.Focus();
            return true;
        }
        if (HelpMenuPopup.IsOpen)
        {
            HelpMenuPopup.IsOpen = false;
            HelpButton.Focus();
            return true;
        }
        if (FindBarRow.Visibility == Visibility.Visible)
        {
            CloseFindbar();
            return true;
        }
        return false;
    }

    // ---------- 目录 ----------

    private void OnToggleToc(object sender, RoutedEventArgs e)
    {
        ToggleToc();
        Reader.Focus();
    }

    private void OnToggleTocFromMenu(object sender, RoutedEventArgs e)
    {
        CloseMenus();
        ToggleToc();
    }

    private void ToggleToc()
    {
        // 目录面板改变 Reader 宽度（重新换行）：与搜索栏同理保留锚点原值。
        Reader.PreserveAnchorOnNextResize = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Reader.PreserveAnchorOnNextResize = false));
        if (TocPanel.Visibility == Visibility.Visible)
        {
            TocPanel.Visibility = Visibility.Collapsed;
            TocCheck.Visibility = Visibility.Collapsed;
            return;
        }
        RebuildTocItems();
        TocPanel.Visibility = Visibility.Visible;
        TocCheck.Visibility = Visibility.Visible;
        // 打开即按锚点定位当前章：设为选中并滚动到可见区（UI-SPEC UI-02）。
        SyncTocToCurrentChapter(scrollIntoView: true);
    }

    /// <summary>目录条目：标题拆为序号与章名，带当前章与已读标记。</summary>
    private sealed class TocItem
    {
        public TocItem(ChapterInfo chapter, int index, bool isCurrent, bool isRead)
        {
            Offset = chapter.Offset;
            Index = index;
            IsCurrent = isCurrent;
            IsRead = isRead;
            string title = chapter.Title;
            int split = title.IndexOfAny(new[] { ' ', '\u3000', '\t' });
            Number = split > 0 ? title[..split] : title;
            Title = split > 0 ? title[(split + 1)..].TrimStart() : title;
            FullTitle = title;
        }

        public int Offset { get; }
        /// <summary>1 起的章序号。</summary>
        public int Index { get; }
        public bool IsCurrent { get; }
        public bool IsRead { get; }
        public string Number { get; }
        public string Title { get; }
        public string FullTitle { get; }
        /// <summary>自定义模板下显示文本走绑定；ToString 供既有自动化/兜底路径使用。</summary>
        public override string ToString() => FullTitle;
    }

    /// <summary>当前锚点所属章节的正文偏移（无章节或无会话时为 -1）。</summary>
    private int CurrentChapterOffset()
    {
        if (_session is null) return -1;
        int offset = -1;
        foreach (var ch in _chapters)
        {
            if (ch.Offset <= _session.Anchor) offset = ch.Offset;
            else break;
        }
        return offset;
    }

    /// <summary>目录同步标记：程序设选中项不触发用户跳章逻辑。</summary>
    private bool _tocSyncing;

    /// <summary>上次同步到目录的当前章偏移；跨章才重建条目，避免打断用户浏览列表。</summary>
    private int _tocSyncedOffset = -1;

    /// <summary>按当前书章节与锚点重建目录条目（含当前章/已读样式标记与空态提示）。</summary>
    /// <summary>按当前书章节与锚点重建目录条目（含当前章/已读样式标记与空态提示）。
    /// 点击跳章后以被点击章覆盖锚点推导的当前章（JumpToOffset 的页首对齐可能把
    /// 锚点留在上一章末页，用户意图优先，审查缺陷 #2）。</summary>
    private void RebuildTocItems(int currentOffsetOverride = int.MinValue)
    {
        int currentOffset = currentOffsetOverride != int.MinValue
            ? currentOffsetOverride
            : CurrentChapterOffset();
        var items = new List<TocItem>(_chapters.Count);
        for (int i = 0; i < _chapters.Count; i++)
        {
            var ch = _chapters[i];
            bool isCurrent = ch.Offset == currentOffset;
            // 已读/未读以当前章为界：当前章（含）之前的章为已读。
            bool isRead = currentOffset >= 0 && ch.Offset <= currentOffset;
            items.Add(new TocItem(ch, i + 1, isCurrent, isRead));
        }
        TocList.ItemsSource = items;
        TocCountText.Text = _chapters.Count == 0 ? string.Empty : $"{_chapters.Count} 章";
        TocEmptyText.Visibility = _chapters.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TocList.Visibility = _chapters.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        _tocSyncedOffset = currentOffset;
    }

    /// <summary>把选中项设为当前章并滚动到可见区；跨章阅读后由 OnReadingAdvanced 再次调用。</summary>
    private void SyncTocToCurrentChapter(bool scrollIntoView)
    {
        if (TocPanel.Visibility != Visibility.Visible) return;
        int currentOffset = CurrentChapterOffset();
        if (currentOffset < 0) return;
        if (currentOffset != _tocSyncedOffset)
        {
            // 跨章：重建条目以刷新当前章与已读/未读标记。
            RebuildTocItems();
        }
        if (TocList.ItemsSource is not IEnumerable<TocItem> items) return;
        TocItem? current = null;
        foreach (var item in items)
        {
            if (item.Offset == currentOffset) { current = item; break; }
        }
        if (current is null) return;
        _tocSyncing = true;
        try
        {
            TocList.SelectedItem = current;
            if (scrollIntoView)
            {
                TocList.ScrollIntoView(current);
            }
        }
        finally
        {
            _tocSyncing = false;
        }
    }

    /// <summary>目录面板打开时随阅读推进同步当前章标记（跨章才重建，避免打断浏览）。</summary>
    private void SyncTocIfOpen()
    {
        if (TocPanel.Visibility != Visibility.Visible) return;
        if (CurrentChapterOffset() == _tocSyncedOffset) return;
        SyncTocToCurrentChapter(scrollIntoView: true);
    }

    private void OnChapterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_tocSyncing) return; // 程序同步选中项：定位动作，不是用户跳章
        if (TocList.SelectedItem is not TocItem item || _session is null) return;
        double ppd = VisualTreeHelper.GetDpi(Reader).DpiScaleX;
        _session.JumpToOffset(item.Offset, Reader.ActualWidth, Reader.ActualHeight, ppd);
        // 落点必须与「当前章」的判定一致：FindPageStartContaining 会落到包含该章的页首，
        // 页首可能仍在上一章（章节标题在该页中间）。此时重开目录按锚点推导又会回到上一章
        // （审查缺陷 #2），因此把锚点收到章首本身——章标题显示在首行。
        if (_session.Anchor < item.Offset) _session.Anchor = item.Offset;
        Reader.Relayout();
        UpdateStatusLabel();
        SaveProgress();
        // 点击跳章后目录本身也要刷新当前章样式（以被点击章覆盖锚点推导结果）。
        // 延迟到布局完成后执行，避免在 SelectionChanged 栈内替换 ItemsSource；
        // 回调必须校验会话与开书代次仍属于本次点击，否则旧回调会覆盖新书/新跳转的目录状态。
        var clickedOffset = item.Offset;
        var clickedSession = _session;
        int openGeneration = _openGeneration;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!ReferenceEquals(clickedSession, _session) || openGeneration != _openGeneration) return;
            if (TocPanel.Visibility != Visibility.Visible) return;
            RebuildTocItems(clickedOffset);
            if (TocList.ItemsSource is not IEnumerable<TocItem> items) return;
            foreach (var entry in items)
            {
                if (entry.Offset != clickedOffset) continue;
                _tocSyncing = true;
                try { TocList.SelectedItem = entry; }
                finally { _tocSyncing = false; }
                break;
            }
        }));
    }

    // ---------- 搜索 ----------

    private void OnSearchFromMenu(object sender, RoutedEventArgs e)
    {
        CloseMenus();
        OpenFindbar();
    }

    private void OpenFindbar()
    {
        // 搜索栏改变 Reader 高度：保留锚点原值，避免页首对齐把阅读位置甩过页边界。
        Reader.PreserveAnchorOnNextResize = true;
        FindBarRow.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Reader.PreserveAnchorOnNextResize = false));
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnCloseFind(object sender, RoutedEventArgs e) => CloseFindbar();

    private void CloseFindbar()
    {
        // 同 OpenFindbar：收起搜索栏时显式保留锚点（Esc 路径也走这里）。
        Reader.PreserveAnchorOnNextResize = true;
        FindBarRow.Visibility = Visibility.Collapsed;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => Reader.PreserveAnchorOnNextResize = false));
        SearchBox.Clear();
        _searchDebounce.Stop(); // 关闭搜索栏（含 Esc）：取消待触发的计数与仍在后台的搜索
        CancelPendingSearch();
        _searchGeneration++;
        if (_session is not null)
        {
            _session.ClearSearch();
            Reader.Relayout();
        }
        SearchStatus.Text = string.Empty;
        Reader.Focus();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) OnSearch(sender, e);
    }

    private async void OnSearch(object sender, RoutedEventArgs e) => RunSearch(SearchBox.Text.Trim(), jumpToFirst: true);

    /// <summary>输入停顿 ~300ms 后的即时计数（UI-06）：只统计并显示「共 N 处」，不跳转。</summary>
    private void OnSearchDebounceTick(object? sender, EventArgs e)
    {
        _searchDebounce.Stop();
        string query = SearchBox.Text.Trim();
        // 查询未变化（如回车搜索完成后迟到的 tick）时不重复统计，
        // 避免覆盖「第 i/N 处」的导航状态。
        if (query == _lastSearchQuery) return;
        RunSearch(query, jumpToFirst: false);
    }

    /// <summary>
    /// 全书搜索（后台）：jumpToFirst 为真时结果就绪后跳到第一处（回车语义），
    /// 否则只补齐命中高亮与计数（停顿计数语义）。
    /// Esc/改查询/换书会取消仍在执行的扫描并作废旧结果，不回填正文。
    /// </summary>
    private async void RunSearch(string query, bool jumpToFirst)
    {
        if (_session is null) return;
        if (query.Length == 0)
        {
            _searchGeneration++;
            _lastSearchQuery = string.Empty;
            CancelPendingSearch();
            _session.ClearSearch();
            SearchStatus.Text = string.Empty;
            Reader.Relayout();
            return;
        }

        int generation = ++_searchGeneration;
        _lastSearchQuery = query;
        var session = _session;
        // 每次搜索持独立取消源：Esc/改查询/换书立即取消旧扫描（审查缺陷 #1）。
        CancelPendingSearch();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;
        SearchStatus.Text = "搜索中…";
        try
        {
            var (hits, length) = await Task.Run(() => BookSearch.FindAll(session.Text, query, token));
            if (generation != _searchGeneration || session != _session) return;
            _session.SetSearchHits(hits, length);
            SearchStatus.Text = hits.Count == 0 ? "无结果" : $"共 {hits.Count} 处";
            if (hits.Count > 0)
            {
                if (jumpToFirst) JumpToHit(0);
                else Reader.Relayout();
            }
            else
            {
                Reader.Relayout();
            }
        }
        catch (OperationCanceledException)
        {
            // 扫描已被更新的请求取消：不回填、不改状态（当前搜索自己负责反馈）。
        }
    }

    private void JumpToHit(int index)
    {
        if (_session is null || _session.SearchHits.Count == 0) return;
        index = (index + _session.SearchHits.Count) % _session.SearchHits.Count;
        _session.ActiveSearchHit = index;
        double ppd = VisualTreeHelper.GetDpi(Reader).DpiScaleX;
        _session.JumpToOffset(_session.SearchHits[index], Reader.ActualWidth, Reader.ActualHeight, ppd);
        Reader.Relayout();
        SearchStatus.Text = $"第 {index + 1}/{_session.SearchHits.Count} 处";
        UpdateStatusLabel();
        SaveProgress();
        SyncTocIfOpen(); // 搜索跳转同样刷新目录当前章（审查缺陷 #2）
    }

    private void OnPrevHit(object sender, RoutedEventArgs e)
    {
        if (_session is { SearchHits.Count: > 0 }) JumpToHit(_session.ActiveSearchHit - 1);
    }

    private void OnNextHit(object sender, RoutedEventArgs e)
    {
        if (_session is { SearchHits.Count: > 0 }) JumpToHit(_session.ActiveSearchHit + 1);
    }

    // ---------- 跳页与翻页 ----------

    private void OnPageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) JumpToPageBox();
    }

    private void JumpToPageBox()
    {
        if (_session is null) return;
        string input = PageBox.Text.Trim();
        double ppd = VisualTreeHelper.GetDpi(Reader).DpiScaleX;
        // 总页数未算好（输入框此时禁用）不允许按页跳转，避免在 UI 线程全书排版。
        if (!_session.HasPageStarts(Reader.ActualWidth, Reader.ActualHeight, ppd)) return;
        if (input.EndsWith('%'))
        {
            // 百分比跳页（UI-14）：含 % 按全书百分比解析，非法输入不跳转、不清空原页码。
            // 落点取当前布局缓存中「最后一个不大于该位置的页首」：锚点总在有效页首，
            // 100% 落到末页正文而非文末空白（审查缺陷 #7）。
            string number = input[..^1].Trim();
            if (!double.TryParse(number, NumberStyles.Float, CultureInfo.CurrentCulture, out double percent)
                || !double.IsFinite(percent) || percent is < 0 or > 100)
            {
                return;
            }
            int offset = Math.Clamp((int)Math.Round(percent / 100.0 * _session.Text.Length), 0, _session.Text.Length);
            var starts = _session.GetPageStarts(Reader.ActualWidth, Reader.ActualHeight, ppd);
            int lo = 0, hi = starts.Count - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (starts[mid] <= offset) lo = mid;
                else hi = mid - 1;
            }
            _session.JumpToPage(lo + 1, Reader.ActualWidth, Reader.ActualHeight, ppd);
        }
        else
        {
            if (!int.TryParse(input, out int pageNumber)) return;
            _session.JumpToPage(pageNumber, Reader.ActualWidth, Reader.ActualHeight, ppd);
        }
        Reader.Relayout();
        UpdateStatusLabel(); // 页码框回显实际页码
        SyncTocIfOpen(); // 跳页/百分比跳转同样刷新目录当前章（审查缺陷 #2）
        SaveProgress();
        Reader.Focus();
    }

    private void OnPrevPage(object sender, RoutedEventArgs e) => StepPage(-1);

    private void OnNextPage(object sender, RoutedEventArgs e) => StepPage(1);

    /// <summary>状态栏上一页/下一页：按页码前进或后退一页（总页数就绪后可用）。</summary>
    private void StepPage(int delta)
    {
        if (_session is null) return;
        double ppd = VisualTreeHelper.GetDpi(Reader).DpiScaleX;
        if (!_session.HasPageStarts(Reader.ActualWidth, Reader.ActualHeight, ppd)) return;
        int page = _session.CachedPageIndex(_session.Anchor);
        int target = Math.Clamp(page + delta, 1, _session.CachedPageCount);
        if (target == page) return;
        _session.JumpToPage(target, Reader.ActualWidth, Reader.ActualHeight, ppd);
        Reader.Relayout();
        UpdateStatusLabel();
        SaveProgress();
        SyncTocIfOpen(); // 按钮翻页与滚轮/方向键一致地刷新目录当前章（审查缺陷 #2）
        Reader.Focus();
    }

    // ---------- 浮窗模式 ----------

    private void OnEnterFloat(object sender, RoutedEventArgs e)
    {
        CloseMenus();
        if (_session is null)
        {
            MessageBox.Show(this, "请先选择一本书。", "HiddenReader");
            return;
        }
        EnterFloatMode();
    }

    private void EnterFloatMode()
    {
        if (_currentBook is null || _session is null) return;
        // 面板不能挂在即将隐藏的宿主上：切模式前先取消预览。
        CloseReadingColorsPanel();
        CloseOpacityPanel();
        _fontSizePanel?.Close();
        SaveProgress();

        if (_float is null)
        {
            var floatState = _library.GetBookState(_currentBook.Id).Float;
            _float = new FloatWindow(_session, floatState);
            _float.ReturnToNormalRequested += OnReturnToNormal;
            _float.ExitRequested += OnExitFromFloat;
            _float.StateChanged += OnFloatStateChanged;
            _float.ReadingColorsRequested += OpenReadingColorsPanel;
            _float.ReadingFontSizeRequested += OpenReadingFontSizePanel;
            _float.OpacityPanelRequested += OpenOpacityPanel;
        }
        _float.Show();
        Hide();
        _library.State.LastMode = "float";
        _library.Save();
    }

    private void OnFloatStateChanged()
    {
        if (_currentBook is null || _float is null || _session is null) return;
        var bookState = _library.GetBookState(_currentBook.Id);
        bookState.Float = _float.CaptureState();
        bookState.Anchor = _session.Anchor; // 浮窗翻页同样保存进度
        _library.Save();
    }

    private void OnReturnToNormal()
    {
        CloseReadingColorsPanel();
        CloseOpacityPanel();
        if (_currentBook is not null && _float is not null)
        {
            _library.GetBookState(_currentBook.Id).Float = _float.CaptureState();
        }
        _float?.Hide();
        Show();
        Activate();
        Reader.Focus();
        _library.State.LastMode = "normal";
        _library.Save();
        UpdateStatusLabel();
    }

    private void OnExitFromFloat()
    {
        if (_currentBook is not null && _float is not null)
        {
            _library.GetBookState(_currentBook.Id).Float = _float.CaptureState();
        }
        _library.State.LastMode = "float";
        _library.Save();
        Diagnostics.Log("exit from float mode");
        _exitingFromFloat = true;
        Application.Current.Shutdown();
    }

    private bool _exitingFromFloat;

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 记录关闭来源便于排查：主动退出、浮窗返回还是外部 WM_CLOSE。
        Diagnostics.Log($"main window closing (exitingFromFloat={_exitingFromFloat})");
        SaveProgress();
        if (_currentBook is not null && _float is not null)
        {
            _library.GetBookState(_currentBook.Id).Float = _float.CaptureState();
        }
        SaveWindowState();
        if (!_exitingFromFloat)
        {
            _library.State.LastMode = "normal";
        }
        _library.Save();
    }

    // ---------- 阅读配色 ----------

    /// <summary>当前打开的配色面板；同一时刻只保留一个。</summary>
    private ReadingColorsWindow? _colorsPanel;

    private void OnOpenReadingColors(object sender, RoutedEventArgs e)
    {
        CloseMenus();
        OpenReadingColorsPanel();
    }

    /// <summary>
    /// 打开阅读配色面板：普通模式以本窗口为宿主；极简浮窗模式以浮窗为宿主并置顶，
    /// 因此浮窗置顶时面板仍在前台可操作。两个入口共用同一个面板。
    /// </summary>
    private void OpenReadingColorsPanel()
    {
        if (_colorsPanel is not null)
        {
            _colorsPanel.Activate();
            return;
        }

        var panel = new ReadingColorsWindow(ReadingColors.ConfirmedText, ReadingColors.ConfirmedBackground,
            ReadingColors.Preview, CommitReadingColors, ReadingColors.RestoreConfirmed);
        if (_float is { } floatWindow)
        {
            // 浮窗是原生 Win32 顶层窗口（可能在置顶带），面板以它的句柄为所有者并自身置顶：
            // 否则面板会被置顶浮窗压住而无法操作。
            panel.Topmost = true;
            panel.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            new System.Windows.Interop.WindowInteropHelper(panel).Owner = floatWindow.Handle;
        }
        else
        {
            panel.Owner = this;
        }

        panel.Closed += (_, _) => _colorsPanel = null;
        _colorsPanel = panel;
        panel.Show();
        panel.Activate();
    }

    /// <summary>
    /// 「确定」：沿用 Library.Save() 的原子保存写入应用级配色。
    /// 保存失败时回滚内存中的状态并把视图恢复为已确认值，面板保留草稿供重试。
    /// </summary>
    private bool CommitReadingColors(Color text, Color background)
    {
        var state = _library.State;
        string previousText = state.TextColor;
        string previousBackground = state.BackgroundColor;
        state.TextColor = ReadingColors.Format(text);
        state.BackgroundColor = ReadingColors.Format(background);
        if (_library.Save())
        {
            ReadingColors.Confirm(text, background);
            return true;
        }
        state.TextColor = previousText;
        state.BackgroundColor = previousBackground;
        ReadingColors.RestoreConfirmed();
        return false;
    }

    /// <summary>切换模式或换书前关闭面板：取消预览，避免面板挂在已隐藏的宿主窗口上。</summary>
    private void CloseReadingColorsPanel()
    {
        if (_colorsPanel is null) return;
        _colorsPanel.Close(); // 未「确定」的关闭按「取消」处理，恢复打开面板前的配色
        _colorsPanel = null;
    }

    // ---------- 浮窗不透明度 ----------

    /// <summary>当前打开的不透明度面板；同一时刻只保留一个。</summary>
    private OpacityPanelWindow? _opacityPanel;

    /// <summary>
    /// 打开浮窗不透明度面板：以浮窗为宿主并随其置顶，保证浮窗置顶时面板在前台可操作
    /// （配色面板同范式）。草稿实时预览只改浮窗渲染，「确定」走原子保存。
    /// </summary>
    private void OpenOpacityPanel(bool focusBackground)
    {
        if (_float is null) return;
        if (_opacityPanel is not null)
        {
            _opacityPanel.Activate();
            return;
        }

        var panel = new OpacityPanelWindow(_float.BackgroundOpacity, _float.BorderOpacity, focusBackground,
            preview: (background, border) => _float?.PreviewOpacity(background, border),
            commit: CommitOpacity,
            cancel: () => _float?.CancelOpacityPreview());
        // 浮窗是原生 Win32 顶层窗口（可能在置顶带），面板以它的句柄为所有者并自身置顶。
        panel.Topmost = _float.Topmost;
        var interop = new System.Windows.Interop.WindowInteropHelper(panel);
        interop.Owner = _float.Handle;
        panel.ContentRendered += (_, _) =>
            WindowPlacement.PlaceReadingPanel(interop.Handle, _float.Handle, avoidOwner: true);
        panel.Closed += (_, _) =>
        {
            _opacityPanel = null;
            // 面板结束后焦点交回浮窗，Esc/方向键继续生效。
            _float?.FocusWindow();
        };
        _opacityPanel = panel;
        panel.Show();
        panel.Activate();
    }

    /// <summary>
    /// 「确定」：先把确认值写入 BookState.Float 并沿用 Library.Save() 原子保存，
    /// 成功后浮窗切确认值；失败回滚状态并让浮窗回到确认值渲染，面板保留草稿。
    /// </summary>
    private bool CommitOpacity(double background, double border)
    {
        if (_float is null || _currentBook is null) return false;
        var floatState = _library.GetBookState(_currentBook.Id).Float;
        double previousBackground = floatState.BackgroundOpacity;
        double previousBorder = floatState.BorderOpacity;
        floatState.BackgroundOpacity = background;
        floatState.BorderOpacity = border;
        if (_library.Save())
        {
            _float.CommitOpacity(background, border);
            return true;
        }
        floatState.BackgroundOpacity = previousBackground;
        floatState.BorderOpacity = previousBorder;
        _float.CancelOpacityPreview();
        return false;
    }

    /// <summary>切换模式或换书前关闭面板：未「确定」的关闭按「取消」处理（恢复确认值）。</summary>
    private void CloseOpacityPanel()
    {
        if (_opacityPanel is null) return;
        _opacityPanel.Close();
        _opacityPanel = null;
    }

    // ---------- 正文字号 ----------

    private void OnOpenReadingFontSize(object sender, RoutedEventArgs e)
    {
        CloseMenus();
        OpenReadingFontSizePanel();
    }

    private void OpenReadingFontSizePanel()
    {
        var floatOwner = IsVisible ? null : _float;
        var panel = new ReadingFontSizeWindow(_readingFontSize, ApplyReadingFontSize, CommitReadingFontSize);
        var interop = new WindowInteropHelper(panel);
        IntPtr ownerHandle;
        if (floatOwner is not null)
        {
            ownerHandle = floatOwner.Handle;
            interop.Owner = ownerHandle;
            panel.Topmost = floatOwner.Topmost;
        }
        else
        {
            panel.Owner = this;
            ownerHandle = new WindowInteropHelper(this).Handle;
        }

        panel.ContentRendered += (_, _) => WindowPlacement.PlaceReadingPanel(interop.Handle, ownerHandle, avoidOwner: floatOwner is not null);
        _fontSizePanel = panel;
        try { panel.ShowDialog(); }
        finally { _fontSizePanel = null; }
        if (floatOwner is null && IsVisible) GearButton.Focus();
    }

    private void ApplyReadingFontSize(int size)
    {
        if (_readingFontSize == size) return;
        _readingFontSize = size;
        if (_session is null) return;

        var settings = _session.Settings.Clone();
        settings.FontSize = size;
        _session.Settings = settings;
        // 从同一个正文偏移量重新换行。逐次对齐页首会让拖动滑块时的阅读位置不断回退。
        Reader.Relayout();
        _float?.Relayout();
        RequestPagination();
        UpdateStatusLabel();
    }

    private bool CommitReadingFontSize(int size)
    {
        var state = _library.State;
        int previous = state.ReadingFontSize;
        state.ReadingFontSize = size;
        if (_library.Save())
        {
            ApplyReadingFontSize(size);
            return true;
        }

        state.ReadingFontSize = previous;
        ApplyReadingFontSize(previous);
        return false;
    }

    /// <summary>启动时若上次以浮窗退出，直接恢复浮窗。</summary>
    public void RestoreFloatModeIfRequested()
    {
        if (_library.State.LastMode == "float" && _currentBook is not null)
        {
            EnterFloatMode();
        }
    }
}
