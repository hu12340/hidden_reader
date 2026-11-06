using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
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

    public MainWindow()
    {
        InitializeComponent();
        // 应用级阅读配色先于任何绘制载入：旧状态缺少颜色字段时使用默认值。
        ReadingColors.Load(_library.State);
        // 标题栏在句柄就绪后由 DWM 着色：系统浅色主题下也保持深色，并随焦点调整文字亮度。
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
        StateChanged += (_, _) => SaveWindowState();
        // 首次定位立即落盘（此后由 StateChanged 增量保存）。
        SaveWindowState();
        // 在物理像素坐标系里核对位置是否仍落在可见工作区
        // （显示器断开或坐标越界时回退，避免窗口恢复到屏幕外）。
        WindowPlacement.EnsureWindowVisible(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);

        Reader.LayoutSizeChanged += OnReaderLayoutChanged;
        RefreshBookList(selectId: state.LastBookId);
    }

    // ---------- 书架 ----------

    private void RefreshBookList(string? selectId)
    {
        BookSelector.ItemsSource = _library.State.Books;
        // 显示文本走 BookInfo.ToString()（=Title）；自定义 ComboBox 模板下
        // DisplayMemberPath 不会作用到条目呈现。
        BookSelector.SelectedItem = _library.State.Books.FirstOrDefault(b => b.Id == selectId);
    }

    private async void OnImport(object sender, RoutedEventArgs e)
    {
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
        try
        {
            var book = await Task.Run(() => _library.Import(dialog.FileName));
            if (_busy)
            {
                // RefreshBookList 会选中新书并触发 OpenBookAsync（自行管理 _busy 与阶段文字）。
                RefreshBookList(book.Id);
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

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (BookSelector.SelectedItem is not BookInfo book) return;
        if (MessageBox.Show(this, $"移除《{book.Title}》？\n仅删除软件管理的副本，原文件不受影响。", "HiddenReader",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        bool removed = _library.Remove(book);
        if (!removed)
        {
            // 书架提交失败（Library.Remove 已回滚内存状态）：书架条目、当前正文、
            // 阅读位置和浮窗全部保持原样，只提示，不刷新列表（避免当前书被重新打开）。
            MessageBox.Show(this, "书架保存失败，移除已取消。\n当前书籍与阅读位置未受影响，请检查磁盘后重试。",
                "HiddenReader", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 提交成功（Remove 返回 true；副本删除失败仅记日志、由启动清理兜底）才走清理路径。
        _openGeneration++; // 使仍在进行的开书结果作废，避免恢复已被移除的书。
        CloseReadingColorsPanel();
        _float?.Dispose();
        _float = null;
        _currentBook = null;
        _session = null;
        Reader.SetSession(null);
        RefreshBookList(_library.State.LastBookId);
        PageLabel.Text = string.Empty;
        SearchStatus.Text = string.Empty;
    }

    private async void OnBookSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BookSelector.SelectedItem is not BookInfo book) return;
        await OpenBookAsync(book);
    }

    private async Task OpenBookAsync(BookInfo book)
    {
        // 换书前关掉配色面板：配色是应用级的，但面板的预览草稿不应跨书残留。
        CloseReadingColorsPanel();
        int generation = ++_openGeneration;
        // 解码与章节识别都在后台线程：期间窗口可重绘、可移动，并显示当前阶段。
        _busy = true;
        BookSelector.IsEnabled = false;
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
        finally
        {
            if (generation == _openGeneration) BookSelector.IsEnabled = true;
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

    /// <summary>
    /// 刷新页码/百分比显示。总页数未算好时显示阅读百分比（锚点/正文长度），
    /// 算好后显示当前布局的准确页码并允许按页跳转。
    /// 不在此处做全书分页——翻页和滚动只做单页排版。
    /// </summary>
    private void UpdateStatusLabel()
    {
        // 导入/打开阶段由流程自己写阶段文字，这里不覆盖。
        if (_busy) return;
        if (_session is null || Reader.ActualWidth < 2)
        {
            PageLabel.Text = string.Empty;
            return;
        }
        double ppd = VisualTreeHelper.GetDpi(Reader).DpiScaleX;
        if (!_session.HasPageStarts(Reader.ActualWidth, Reader.ActualHeight, ppd))
        {
            double fraction = _session.Text.Length > 0 ? (double)_session.Anchor / _session.Text.Length : 0;
            PageLabel.Text = $"阅读 {fraction * 100:0.#}%　总页数计算中…";
            PageBox.IsEnabled = false;
            return;
        }
        int page = _session.CachedPageIndex(_session.Anchor);
        int total = _session.CachedPageCount;
        string? chapter = CurrentChapterTitle();
        PageLabel.Text = $"第 {page}/{total} 页" + (chapter is null ? "" : $"　{chapter}");
        PageBox.IsEnabled = true;
        PageBox.Text = page.ToString();
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
        // 文本输入控件（搜索框、跳页框）保留系统编辑行为，Ctrl+C 不得拦截。
        if (SearchBox.IsKeyboardFocused || PageBox.IsKeyboardFocused) return;

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

    // ---------- 目录 ----------

    private void OnToggleToc(object sender, RoutedEventArgs e)
    {
        if (TocList.Visibility == Visibility.Visible)
        {
            TocList.Visibility = Visibility.Collapsed;
            return;
        }
        TocList.ItemsSource = _chapters.Select(c => new TocItem(c.Offset, c.Title)).ToList();
        TocList.Visibility = Visibility.Visible;
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

    // ---------- 跳页 ----------

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

    // ---------- 浮窗模式 ----------

    private void OnEnterFloat(object sender, RoutedEventArgs e)
    {
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

    private void OnOpenReadingColors(object sender, RoutedEventArgs e) => OpenReadingColorsPanel();

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
