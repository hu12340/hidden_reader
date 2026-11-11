using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HiddenReader.App.UI;

/// <summary>
/// 浮窗不透明度面板：沿用配色面板既有范式（UI-SPEC 2.4）——
/// 滑杆拖动实时预览草稿，「确定」交给宿主原子保存，「取消」恢复打开面板前的值。
/// </summary>
public partial class OpacityPanelWindow : Window
{
    private readonly Action<double, double> _preview;
    private readonly Func<double, double, bool> _commit;
    private readonly Action _cancel;
    private bool _confirmed;

    internal OpacityPanelWindow(double background, double border, bool focusBackground,
        Action<double, double> preview, Func<double, double, bool> commit, Action cancel)
    {
        _preview = preview;
        _commit = commit;
        _cancel = cancel;
        InitializeComponent();
        // 与普通窗口一致的深色标题栏（系统浅色主题下也保持深色）。
        DarkTitleBar.Attach(this);
        BackgroundSlider.Value = Math.Round(Math.Clamp(background, 0, 1) * 100);
        BorderSlider.Value = Math.Round(Math.Clamp(border, 0, 1) * 100);
        UpdateValueLabels();
        BackgroundSlider.ValueChanged += OnSliderValueChanged;
        BorderSlider.ValueChanged += OnSliderValueChanged;
        Loaded += (_, _) => (focusBackground ? BackgroundSlider : BorderSlider).Focus();
        // Esc 等同「取消」：恢复打开面板前的值。
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            Close();
            e.Handled = true;
        };
    }

    private void OnSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        UpdateValueLabels();
        _preview(BackgroundSlider.Value / 100, BorderSlider.Value / 100);
    }

    private void UpdateValueLabels()
    {
        BackgroundValue.Text = $"{(int)Math.Round(BackgroundSlider.Value)}%";
        BorderValue.Text = $"{(int)Math.Round(BorderSlider.Value)}%";
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (_commit(BackgroundSlider.Value / 100, BorderSlider.Value / 100))
        {
            _confirmed = true;
            Close();
            return;
        }

        // 保存失败：宿主已把视图恢复为已确认值，草稿仍留在面板里供重试。
        MessageBox.Show(this,
            "不透明度保存失败，已恢复上次保存的值。\n面板保留当前草稿，可检查磁盘后重试。",
            "HiddenReader", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    protected override void OnClosed(EventArgs e)
    {
        // 关闭按钮与 Esc 同「取消」；确定成功后不再恢复。
        if (!_confirmed) _cancel();
        base.OnClosed(e);
    }
}
