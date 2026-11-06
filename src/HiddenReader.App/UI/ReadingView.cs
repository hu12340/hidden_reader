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

    public event Action? SessionAdvanced;

    public ReadingView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
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
    }

    private static readonly Brush PageBackground = FreezeBrush(new SolidColorBrush(Color.FromRgb(0x1E, 0x1F, 0x26)));

    private static Brush FreezeBrush(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(PageBackground, null, new Rect(0, 0, width, height));
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

        PageRenderer.DrawPage(dc, _page, PageRenderer.DefaultTextBrush, CollectPageHighlights(_page), selection);
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
