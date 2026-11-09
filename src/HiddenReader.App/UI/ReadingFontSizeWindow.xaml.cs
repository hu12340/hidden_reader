using System.Windows;
using HiddenReader.Core.Reading;

namespace HiddenReader.App.UI;

public partial class ReadingFontSizeWindow : Window
{
    private readonly int _initialSize;
    private readonly Action<int> _preview;
    private readonly Func<int, bool> _commit;

    internal ReadingFontSizeWindow(int size, Action<int> preview, Func<int, bool> commit)
    {
        _initialSize = size;
        _preview = preview;
        _commit = commit;
        InitializeComponent();
        DarkTitleBar.Attach(this);
        FontSizeSlider.Maximum = TextSettings.MaximumFontSize;
        FontSizeSlider.Minimum = TextSettings.MinimumFontSize;
        FontSizeSlider.Value = size;
        UpdateSizeControls(size);
        FontSizeSlider.ValueChanged += (_, e) =>
        {
            int draft = (int)Math.Round(e.NewValue);
            UpdateSizeControls(draft);
            _preview(draft);
        };
    }

    private void UpdateSizeControls(int size)
    {
        SizeValue.Text = size.ToString();
        DecreaseButton.IsEnabled = size > TextSettings.MinimumFontSize;
        IncreaseButton.IsEnabled = size < TextSettings.MaximumFontSize;
    }

    private void OnDecrease(object sender, RoutedEventArgs e) => FontSizeSlider.Value--;

    private void OnIncrease(object sender, RoutedEventArgs e) => FontSizeSlider.Value++;

    private void OnRestoreDefaults(object sender, RoutedEventArgs e) => FontSizeSlider.Value = TextSettings.DefaultFontSize;

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_commit((int)Math.Round(FontSizeSlider.Value)))
        {
            DialogResult = true;
            return;
        }

        MessageBox.Show(this, "字号保存失败，已恢复上次保存的字号。\n面板保留当前字号，可检查磁盘后重试。",
            "HiddenReader", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnClosed(EventArgs e)
    {
        if (DialogResult != true) _preview(_initialSize);
        base.OnClosed(e);
    }
}
