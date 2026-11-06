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
    private ReadingSession? _session;
    private BookInfo? _currentBook;
    private List<ChapterInfo> _chapters = new();
    private FloatWindow? _float;

    public MainWindow()
    {
        InitializeComponent();
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

    private void OnImport(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入 TXT 小说",
            Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            var book = _library.Import(dialog.FileName);
            RefreshBookList(book.Id);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "导入失败：" + ex.Message, "HiddenReader");
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
        _float?.Dispose();
        _float = null;
        _currentBook = null;
        _session = null;
        Reader.SetSession(null);
        RefreshBookList(_library.State.LastBookId);
        PageLabel.Text = string.Empty;
        SearchStatus.Text = string.Empty;
    }

    private void OnBookSelected(object sender, SelectionChangedEventArgs e)
    {
        if (BookSelector.SelectedItem is not BookInfo book) return;
        OpenBook(book);
    }

    private void OpenBook(BookInfo book)
    {
        try
        {
            // 切书时旧的浮窗绑定的是上一本书的会话，直接丢弃。
            _float?.Dispose();
            _float = null;
            string text = _library.ReadBook(book);
            _currentBook = book;
            _session = new ReadingSession(text);
            var bookState = _library.GetBookState(book.Id);
            _session.Anchor = Math.Clamp(bookState.Anchor, 0, text.Length);
            _chapters = Chapters.Detect(text);
            Diagnostics.Log($"book '{book.Title}' chars={text.Length} chapters={_chapters.Count}");
            Reader.SetSession(_session);
            Reader.Focus();
            _library.State.LastBookId = book.Id;
            _library.Save();
            UpdateStatusLabel();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "打开书籍失败：" + ex.Message, "HiddenReader");
        }
    }

    private void OnReadingAdvanced()
    {
        UpdateStatusLabel();
        SaveProgress();
    }

    private void UpdateStatusLabel()
    {
        if (_session is null || Reader.ActualWidth < 2)
        {
            PageLabel.Text = string.Empty;
            return;
        }
        double ppd = VisualTreeHelper.GetDpi(Reader).DpiScaleX;
        int page = PageIndexOf(_session.Anchor);
        int total = _session.PageCount(Reader.ActualWidth, Reader.ActualHeight, ppd);
        string chapter = CurrentChapterTitle();
        PageLabel.Text = $"第 {page}/{total} 页" + (chapter is null ? "" : $"　{chapter}");
        PageBox.Text = page.ToString();
    }

    private int PageIndexOf(int anchor)
    {
        if (_session is null) return 1;
        double ppd = VisualTreeHelper.GetDpi(Reader).DpiScaleX;
        var starts = _session.PageStarts(Reader.ActualWidth, Reader.ActualHeight, ppd);
        int index = starts.LastIndexOf(anchor);
        return index >= 0 ? index + 1 : 1;
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
        SaveProgress();

        if (_float is null)
        {
            var floatState = _library.GetBookState(_currentBook.Id).Float;
            _float = new FloatWindow(_session, floatState);
            _float.ReturnToNormalRequested += OnReturnToNormal;
            _float.ExitRequested += OnExitFromFloat;
            _float.StateChanged += OnFloatStateChanged;
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

    /// <summary>启动时若上次以浮窗退出，直接恢复浮窗。</summary>
    public void RestoreFloatModeIfRequested()
    {
        if (_library.State.LastMode == "float" && _currentBook is not null)
        {
            EnterFloatMode();
        }
    }
}
