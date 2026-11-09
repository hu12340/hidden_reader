using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
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
    /// <summary>导入/打开期间为真：状态栏显示阶段文字，期间不刷新页码。</summary>
    private bool _busy;
    /// <summary>开书序号：切换书籍或重复选择时使旧的异步结果作废。</summary>
    private int _openGeneration;
    /// <summary>刚被同一次外击关闭的菜单弹层：按下触发按钮时 StaysOpen=False 弹层先关闭，
    /// 随后的 Click 若立即重开就成了「点不开」，据此识别为关闭请求。
    /// 只在极短时间窗内生效，否则标记残留会让下一次点开菜单失效。</summary>
    private Popup? _menuJustClosedSource;
    private DateTime _menuJustClosedAt;

    public MainWindow()
    {
        InitializeComponent();
        // 应用级阅读配色先于任何绘制载入：旧状态缺少颜色字段时使用默认值。
        ReadingColors.Load(_library.State);
        // 标题栏在句柄就绪后由 DWM 着色：系统浅色主题下也保持深色，并随焦点调整文字亮度。
        // 顶部栏改为客户区自绘后，DWM 标题栏不再可见；保留着色使窗口边缘细节保持深色。
        DarkTitleBar.Attach(this);
        // 分页结果回调统一封送回 UI 线程。
        _pager = new BackgroundPager(result => Dispatcher.BeginInvoke(() => ApplyPageStarts(result)));
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
            SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // 首次定位立即落盘（此后由 StateChanged 增量保存）。
        SaveWindowState();
        // 在物理像素坐标系里核对位置是否仍落在可见工作区
        // （显示器断开或坐标越界时回退，避免窗口恢复到屏幕外）。
        WindowPlacement.EnsureWindowVisible(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);

        Reader.LayoutSizeChanged += OnReaderLayoutChanged;
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

    private void OnFileMenuPopupClosed(object? sender, EventArgs e)
    {
        _menuJustClosedSource = FileMenuPopup;
        _menuJustClosedAt = DateTime.UtcNow;
    }

    private void OnViewMenuPopupClosed(object? sender, EventArgs e)
    {
        _menuJustClosedSource = ViewMenuPopup;
        _menuJustClosedAt = DateTime.UtcNow;
    }

    private void ToggleMenu(Popup popup)
    {
        if (_menuJustClosedSource == popup
            && (DateTime.UtcNow - _menuJustClosedAt).TotalMilliseconds < 400)
        {
            // 同一次按下先关闭了它：本次点击视为关闭请求，不立即重开。
            _menuJustClosedSource = null;
            return;
        }
        _menuJustClosedSource = null;
        CloseMenus();
        popup.IsOpen = true;
    }

    /// <summary>收起两个菜单弹层；不触碰焦点归属（由各入口自行决定）。</summary>
    private void CloseMenus()
    {
        FileMenuPopup.IsOpen = false;
        ViewMenuPopup.IsOpen = false;
    }

    // ---------- 书架 ----------

    private sealed record ShelfItem(BookInfo Book, bool IsCurrent);

    private void RefreshShelf()
    {
        var books = _library.State.Books;
        ShelfList.ItemsSource = books.Select(b => new ShelfItem(b, b.Id == _currentBook?.Id)).ToList();
        ShelfCountText.Text = books.Count + " 本";
        ShelfEmptyText.Visibility = books.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
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
        _menuJustClosedSource = null;
        ViewMenuPopup.IsOpen = false;
        FileMenuPopup.IsOpen = true;
    }

    private async Task OpenBookAsync(BookInfo book)
    {
        // 换书前关掉配色面板：配色是应用级的，但面板的预览草稿不应跨书残留。
        CloseReadingColorsPanel();
        int generation = ++_openGeneration;
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

            // 启动恢复：App.OnStartup 在异步开书完成前调用过一次（当时还没有会话），
            // 会话就绪后在这里补齐浮窗恢复，不必等章节识别。
            RestoreFloatModeIfRequested();

            PageLabel.Text = $"正在打开《{book.Title}》：识别章节…";
            _chapters = await Task.Run(() => Chapters.Detect(text));
            if (generation != _openGeneration) return;
            Diagnostics.Log($"book '{book.Title}' chars={text.Length} chapters={_chapters.Count}");
            UpdateStatusLabel();
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
        // 会话已被替换（切书）或布局尺寸已变化（缩放）的过期结果直接丢弃。
        if (_session != result.Session) return;
        if (_session is null || Reader.ActualWidth < 2 || Reader.ActualHeight < 2) return;
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
            PageLabel.Text = _currentBook is null ? "从「文件」导入 TXT 小说" : string.Empty;
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
        string? chapter = CurrentChapterTitle();
        PageLabel.Text = chapter ?? string.Empty;
        PageTotal.Text = $"/ {total} 页";
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

        // 菜单打开时按键属于菜单交互，不进入正文翻页。
        if (FileMenuPopup.IsOpen || ViewMenuPopup.IsOpen) return;

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
            _menuJustClosedSource = null;
            FileButton.Focus();
            return true;
        }
        if (ViewMenuPopup.IsOpen)
        {
            ViewMenuPopup.IsOpen = false;
            _menuJustClosedSource = null;
            ViewButton.Focus();
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
        TocList.ItemsSource = _chapters.Select(c => new TocItem(c.Offset, c.Title)).ToList();
        TocPanel.Visibility = Visibility.Visible;
        TocCheck.Visibility = Visibility.Visible;
    }

    private sealed record TocItem(int Offset, string Title)
    {
        public override string ToString() => Title;
    }

    private void OnChapterSelected(object sender, SelectionChangedEventArgs e)
    {
        if (TocList.SelectedItem is not TocItem item || _session is null) return;
        double ppd = VisualTreeHelper.GetDpi(Reader).DpiScaleX;
        _session.JumpToOffset(item.Offset, Reader.ActualWidth, Reader.ActualHeight, ppd);
        Reader.Relayout();
        UpdateStatusLabel();
        SaveProgress();
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

    private void OnSearch(object sender, RoutedEventArgs e)
    {
        if (_session is null) return;
        string query = SearchBox.Text.Trim();
        if (query.Length == 0)
        {
            _session.ClearSearch();
            SearchStatus.Text = string.Empty;
            Reader.Relayout();
            return;
        }

        var (hits, length) = BookSearch.FindAll(_session.Text, query);
        _session.SetSearchHits(hits, length);
        SearchStatus.Text = hits.Count == 0 ? "无结果" : $"共 {hits.Count} 处";
        if (hits.Count > 0)
        {
            JumpToHit(0);
        }
        else
        {
            Reader.Relayout();
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
        if (!int.TryParse(PageBox.Text.Trim(), out int pageNumber)) return;
        double ppd = VisualTreeHelper.GetDpi(Reader).DpiScaleX;
        // 总页数未算好（输入框此时禁用）不允许按页跳转，避免在 UI 线程全书排版。
        if (!_session.HasPageStarts(Reader.ActualWidth, Reader.ActualHeight, ppd)) return;
        _session.JumpToPage(pageNumber, Reader.ActualWidth, Reader.ActualHeight, ppd);
        Reader.Relayout();
        UpdateStatusLabel();
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
        SaveProgress();

        if (_float is null)
        {
            var floatState = _library.GetBookState(_currentBook.Id).Float;
            _float = new FloatWindow(_session, floatState);
            _float.ReturnToNormalRequested += OnReturnToNormal;
            _float.ExitRequested += OnExitFromFloat;
            _float.StateChanged += OnFloatStateChanged;
            _float.ReadingColorsRequested += OpenReadingColorsPanel;
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

    /// <summary>启动时若上次以浮窗退出，直接恢复浮窗。</summary>
    public void RestoreFloatModeIfRequested()
    {
        if (_library.State.LastMode == "float" && _currentBook is not null)
        {
            EnterFloatMode();
        }
    }
}
