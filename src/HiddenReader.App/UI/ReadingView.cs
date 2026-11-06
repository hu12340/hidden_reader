using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HiddenReader.Core.Reading;

namespace HiddenReader.App.UI;

/// <summary>
/// 普通模式阅读视图：与浮窗共用 PageRenderer/分页逻辑。
/// 单击翻到下一页，拖动选择文字，Ctrl+C 复制；窗口重排时保持阅读位置。
/// </summary>
public sealed class ReadingView : FrameworkElement
{
    private ReadingSession? _session;
    private PageLayout? _page;
    private int _selectionAnchor = -1;
    private int _selectionFocus = -1;
    private bool _selecting;
    private Point _downPoint;
    private SolidColorBrush _textBrush = CreateBrush(ReadingColors.Text);
    private SolidColorBrush _backgroundBrush = CreateBrush(ReadingColors.Background);

    public event Action? SessionAdvanced;
    /// <summary>视图尺寸变化（重排保持位置后触发），宿主据此重新请求后台全书分页。</summary>
    public event Action? LayoutSizeChanged;

    public ReadingView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
        ReadingColors.Changed += OnColorsChanged;
        Unloaded += (_, _) => ReadingColors.Changed -= OnColorsChanged;
    }

    /// <summary>
    /// 配色变化只重绘当前页：保留 _page、阅读锚点与选区，不做重排。
    /// 重排只在尺寸或锚点变化时发生，避免预览配色时阅读位置跳动。
    /// </summary>
    private void OnColorsChanged()
    {
        _textBrush = CreateBrush(ReadingColors.Text);
        _backgroundBrush = CreateBrush(ReadingColors.Background);
        InvalidateVisual();
    }

    private static SolidColorBrush CreateBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    public void SetSession(ReadingSession? session)
    {
        _session = session;
        _selectionAnchor = -1;
        _selectionFocus = -1;
        _page = null;
        InvalidateVisual();
    }

    public void Relayout()
    {
        _page = null;
        InvalidateVisual();
    }

    private double Scale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        if (_session is not null && sizeInfo.NewSize.Width > 1 && sizeInfo.NewSize.Height > 1)
        {
            double ppd = Scale;
            // 重排后保持阅读位置。
            _session.JumpToOffset(_session.Anchor, sizeInfo.NewSize.Width, sizeInfo.NewSize.Height, ppd);
        }
        _page = null;
        InvalidateVisual();
        LayoutSizeChanged?.Invoke();
    }

    /// <summary>
    /// 滚轮连续滚动正文，可停在整页边界之间。滚轮事件只有光标位于本视图时
    /// 才会路由进来，正文区外转动滚轮天然不影响正文。
    /// </summary>
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_session is null) { base.OnMouseWheel(e); return; }
        int notches = e.Delta / 120;
        if (notches == 0 && e.Delta != 0) notches = Math.Sign(e.Delta);
        if (notches == 0) { base.OnMouseWheel(e); return; }
        int linesPerNotch = WheelInput.LinesPerNotch();
        // 上限防止「一次滚动多屏」的滚轮把读者甩出视野。
        int lines = Math.Clamp(Math.Abs(notches) * linesPerNotch, 1, 30);
        // 滚轮符号约定（与浮窗一致）：正值 = 远离自己，负值 = 朝自己。
        // 朝自己拨（负增量）要看到后文、文字上移，所以负增量走 ScrollDownLines。
        bool moved = notches < 0
            ? _session.ScrollDownLines(lines, ActualWidth, ActualHeight, Scale)
            : _session.ScrollUpLines(lines, ActualWidth, ActualHeight, Scale);
        if (moved)
        {
            // 滚动后清除旧选区（选区随页消失，避免残留错误高亮）。
            _selectionAnchor = -1;
            _selectionFocus = -1;
            _page = null;
            InvalidateVisual();
            SessionAdvanced?.Invoke();
        }
        e.Handled = true;
    }

    // 正文底色取应用级阅读配色（需求第 7 节）：普通窗口始终不透明，
    // 与极简浮窗共用一套已确认的配色，不随系统主题变化。
    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(_backgroundBrush, null, new Rect(0, 0, width, height));
        if (_session is null) return;

        if (_page is null)
        {
            double ppd = Scale;
            _page = Paginator.Layout(_session.Text, _session.Anchor, width, height, _session.Settings, ppd);
        }

        HighlightRange? selection = null;
        if (_selectionAnchor >= 0 && _selectionFocus >= 0 && _selectionAnchor != _selectionFocus)
        {
            selection = new HighlightRange(Math.Min(_selectionAnchor, _selectionFocus), Math.Abs(_selectionFocus - _selectionAnchor));
        }

        PageRenderer.DrawPage(dc, _page, _textBrush, CollectPageHighlights(_page), selection);
    }

    private IReadOnlyList<HighlightRange> CollectPageHighlights(PageLayout page)
    {
        if (_session is null || _session.SearchHits.Count == 0) return Array.Empty<HighlightRange>();
        var ranges = new List<HighlightRange>();
        int pageEnd = page.End;
        var hits = _session.SearchHits;
        for (int i = 0; i < hits.Count; i++)
        {
            int hit = hits[i];
            if (hit + _session.SearchHitLength <= page.Start) continue;
            if (hit >= pageEnd) break;
            ranges.Add(new HighlightRange(hit, _session.SearchHitLength));
        }
        return ranges;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        var pos = e.GetPosition(this);
        _downPoint = pos;
        _selecting = true;
        _selectionAnchor = HitTest(pos);
        _selectionFocus = _selectionAnchor;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_selecting) return;
        var pos = e.GetPosition(this);
        int focus = HitTest(pos);
        if (focus != _selectionFocus)
        {
            _selectionFocus = focus;
            InvalidateVisual();
        }
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_selecting) return;
        _selecting = false;
        ReleaseMouseCapture();
        var pos = e.GetPosition(this);
        double distance = Math.Abs(pos.X - _downPoint.X) + Math.Abs(pos.Y - _downPoint.Y);
        if (distance <= 3)
        {
            // 单击：翻到下一页，同时清除旧选区。
            _selectionAnchor = -1;
            _selectionFocus = -1;
            _session?.NextPage(ActualWidth, ActualHeight, Scale);
            _page = null;
            InvalidateVisual();
            SessionAdvanced?.Invoke();
        }
        else
        {
            // 拖动选字：松手后保留高亮，供 Ctrl+C 复制；不翻页。
            InvalidateVisual();
        }
        e.Handled = true;
    }

    /// <summary>由宿主在窗口有焦点时转发的方向键。</summary>
    public bool HandleKey(Key key)
    {
        if (_session is null) return false;
        switch (key)
        {
            case Key.Left:
            case Key.Up:
                _session.PreviousPage(ActualWidth, ActualHeight, Scale);
                break;
            case Key.Right:
            case Key.Down:
                _session.NextPage(ActualWidth, ActualHeight, Scale);
                break;
            default:
                return false;
        }
        // 翻页后清除旧选区（选区随页消失，避免残留错误高亮）。
        _selectionAnchor = -1;
        _selectionFocus = -1;
        _page = null;
        InvalidateVisual();
        SessionAdvanced?.Invoke();
        return true;
    }

    public string? GetSelectedText()
    {
        if (_session is null || _selectionAnchor < 0 || _selectionFocus < 0) return null;
        int start = Math.Min(_selectionAnchor, _selectionFocus);
        int end = Math.Max(_selectionAnchor, _selectionFocus);
        return end > start ? _session.Text[start..end] : null;
    }

    private int HitTest(Point pos)
    {
        if (_page is null) return _session?.Anchor ?? 0;
        return PageRenderer.HitTest(_page, pos);
    }
}
