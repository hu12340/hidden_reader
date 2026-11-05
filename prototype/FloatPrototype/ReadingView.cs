using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HiddenReader.Core.Reading;

namespace FloatPrototype;

/// <summary>普通窗口中的阅读区域，与浮窗共用分页与渲染。</summary>
public sealed class ReadingView : FrameworkElement
{
    private readonly PrototypeSession _session;
    private PageLayout? _page;
    private Point _downPoint;
    private bool _pressed;

    public event Action? PageChanged;

    public ReadingView(PrototypeSession session)
    {
        _session = session;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Background = Brushes.White;
    }

    public Brush Background { get; set; }

    public PageLayout? Page => _page;

    public void Refresh()
    {
        _page = null;
        InvalidateVisual();
        if (ActualWidth > 1)
        {
            App.Log($"shell render anchor={_session.Anchor} size={ActualWidth:F0}x{ActualHeight:F0}");
        }
        PageChanged?.Invoke();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Background, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth < 20 || ActualHeight < 20) return;

        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_page is null || Math.Abs(_page.ContentWidth - ActualWidth) > 0.5 || Math.Abs(_page.ContentHeight - ActualHeight) > 0.5)
        {
            _page = Paginator.Layout(_session.Text, _session.Anchor, ActualWidth, ActualHeight, _session.Settings, ppd);
        }

        PageRenderer.DrawPage(dc, _page, PageRenderer.DefaultTextBrush);
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        _downPoint = e.GetPosition(this);
        _pressed = true;
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_pressed) return;
        _pressed = false;
        ReleaseMouseCapture();
        var point = e.GetPosition(this);
        if (Math.Abs(point.X - _downPoint.X) + Math.Abs(point.Y - _downPoint.Y) > 4) return;

        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        _session.NextPage(ActualWidth, ActualHeight, ppd);
        Refresh();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (e.Key == Key.Right)
        {
            _session.NextPage(ActualWidth, ActualHeight, ppd);
            Refresh();
            e.Handled = true;
        }
        else if (e.Key == Key.Left)
        {
            _session.PreviousPage(ActualWidth, ActualHeight, ppd);
            Refresh();
            e.Handled = true;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        Refresh();
    }
}
