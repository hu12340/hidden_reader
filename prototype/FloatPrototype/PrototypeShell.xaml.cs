using System.Windows;
using System.Windows.Input;
using FloatPrototype.Float;

namespace FloatPrototype;

public partial class PrototypeShell : Window
{
    private readonly PrototypeSession _session = new();
    private readonly ReadingView _reader;
    private FloatWindow? _floatWindow;

    public PrototypeShell()
    {
        InitializeComponent();
        _reader = new ReadingView(_session);
        _reader.PageChanged += UpdatePageLabel;
        ReaderHost.Children.Add(_reader);
        Loaded += (_, _) =>
        {
            UpdatePageLabel();
            _reader.Focus();
            if (App.AutoFloat) EnterFloatMode();
        };
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (_, _) => Application.Current.Shutdown();
    }

    private void UpdatePageLabel()
    {
        int page = _session.PageNumber(Math.Max(1, _reader.ActualWidth), Math.Max(1, _reader.ActualHeight),
            System.Windows.Media.VisualTreeHelper.GetDpi(_reader).PixelsPerDip);
        int total = _session.PageCount(Math.Max(1, _reader.ActualWidth), Math.Max(1, _reader.ActualHeight),
            System.Windows.Media.VisualTreeHelper.GetDpi(_reader).PixelsPerDip);
        PageLabel.Text = $"第 {page} / {total} 页　（锚点偏移 {_session.Anchor}）";
    }

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        _session.PreviousPage(_reader.ActualWidth, _reader.ActualHeight, System.Windows.Media.VisualTreeHelper.GetDpi(_reader).PixelsPerDip);
        _reader.Refresh();
        _reader.Focus();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        _session.NextPage(_reader.ActualWidth, _reader.ActualHeight, System.Windows.Media.VisualTreeHelper.GetDpi(_reader).PixelsPerDip);
        _reader.Refresh();
        _reader.Focus();
    }

    private void OnFloatClick(object sender, RoutedEventArgs e) => EnterFloatMode();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F2)
        {
            EnterFloatMode();
            e.Handled = true;
        }
    }

    private void EnterFloatMode()
    {
        Hide();
        if (_floatWindow is null)
        {
            _floatWindow = new FloatWindow(_session);
            _floatWindow.ExitToNormalRequested += ExitFloatMode;
            _floatWindow.Closed += () => Application.Current.Shutdown();
        }
        _floatWindow.Show();
    }

    private void ExitFloatMode()
    {
        _floatWindow?.Hide();
        Show();
        Activate();
        _reader.Refresh();
        _reader.Focus();
    }
}
