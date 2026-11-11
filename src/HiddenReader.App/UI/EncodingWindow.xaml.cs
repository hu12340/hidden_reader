using System.Windows;
using System.Windows.Controls;
using HiddenReader.App.Data;

namespace HiddenReader.App.UI;

/// <summary>
/// 编码切换对话框（UI-04）：列出候选编码，返回所选键（SelectedEncoding，null = 自动识别）。
/// </summary>
public partial class EncodingWindow : Window
{
    /// <summary>确定后的选择：null = 自动识别，否则为 Library.EncodingChoices 中的键。</summary>
    public string? SelectedEncoding { get; private set; }

    public EncodingWindow(string bookTitle, string? currentKey)
    {
        InitializeComponent();
        // 与普通窗口一致的深色标题栏（系统浅色主题下也保持深色）。
        DarkTitleBar.Attach(this);
        BookTitleText.Text = bookTitle;
        foreach (var (displayName, key) in Library.EncodingChoices)
        {
            var option = new RadioButton
            {
                Content = displayName,
                GroupName = "Encoding",
                Tag = key,
                Margin = new Thickness(0, 4, 0, 4),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE5, 0xE8, 0xEE)),
            };
            bool isAuto = key.Length == 0;
            option.IsChecked = currentKey is null ? isAuto : key == currentKey;
            OptionsHost.Children.Add(option);
        }
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (OptionsHost.Children.OfType<RadioButton>().FirstOrDefault(r => r.IsChecked == true) is not { } selected)
        {
            DialogResult = false;
            return;
        }
        string key = (string)selected.Tag;
        SelectedEncoding = key.Length == 0 ? null : key;
        DialogResult = true;
    }
}
