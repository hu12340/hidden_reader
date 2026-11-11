using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HiddenReader.App.UI;

/// <summary>
/// 应用内帮助窗口：快速上手与键盘快捷键共用同一骨架，
/// 行内容由工厂方法（CreateGuide / CreateShortcuts）构建。
/// </summary>
public partial class HelpWindow : Window
{
    /// <summary>一行帮助内容：Term 为条目名或快捷键，TermIsKey 时按键帽样式渲染。</summary>
    public sealed record Row(string Term, string Description, bool TermIsKey = false);

    private HelpWindow(string title, string? intro, IReadOnlyList<Row> rows)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        if (intro is not null)
        {
            IntroText.Text = intro;
            IntroText.Visibility = Visibility.Visible;
        }
        foreach (var row in rows)
        {
            Body.Children.Add(BuildRow(row));
        }
    }

    public static HelpWindow CreateGuide() => new(
        "快速上手",
        "HiddenReader 是一款本地 TXT 小说阅读器。",
        new[]
        {
            new Row("打开小说", "点顶部的书页图标导入 TXT，或从书架选择小说继续阅读。"),
            new Row("查找正文", "点眼睛图标打开目录或搜索。"),
            new Row("阅读设置", "点齿轮图标调整阅读配色或正文字号。"),
            new Row("极简浮窗", "点最右侧的浮窗图标进入极简模式。进入后用滚轮阅读，右键可调整字号，按 Esc 返回普通模式。" +
                "移动浮窗：按住 Ctrl，在浮窗正文区按住鼠标左键拖动。"),
        });

    public static HelpWindow CreateShortcuts() => new(
        "键盘快捷键",
        null,
        new[]
        {
            new Row("Ctrl+O", "在普通模式导入 TXT。", TermIsKey: true),
            new Row("Ctrl+F", "在普通模式搜索当前小说。", TermIsKey: true),
            new Row("← / →", "正文窗口有焦点时向前／向后翻页。", TermIsKey: true),
            new Row("Esc", "收起菜单、关闭搜索栏，或在极简浮窗中返回普通模式。", TermIsKey: true),
            // 浮窗三键此前不可发现（REVIEW P1-5）：仅浮窗有焦点时生效；F5/F6 为三档循环快捷方式，
            // 连续调节走浮窗右键菜单的「背景/边框不透明度」面板（UI-03）。
            new Row("F5", "仅浮窗有焦点时生效：循环背景不透明度 100%→50%→0%（三档快捷方式，连续调节用浮窗右键菜单）。", TermIsKey: true),
            new Row("F6", "仅浮窗有焦点时生效：循环边框不透明度 100%→50%→0%（三档快捷方式，连续调节用浮窗右键菜单）。", TermIsKey: true),
            new Row("F7", "仅浮窗有焦点时生效：切换浮窗置顶。", TermIsKey: true),
        });

    private static UIElement BuildRow(Row row)
    {
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        FrameworkElement term;
        if (row.TermIsKey)
        {
            // 键帽样式与原型一致：深底描边 + 等宽字体。
            term = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x1B, 0x20, 0x29)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x46, 0x54, 0x68)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(5, 1, 5, 1),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Child = new TextBlock
                {
                    Text = row.Term,
                    Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xE5, 0xFB)),
                    FontFamily = new FontFamily("Consolas"),
                    FontSize = 12,
                },
            };
        }
        else
        {
            term = new TextBlock
            {
                Text = row.Term,
                Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xE5, 0xFB)),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Top,
            };
        }

        var description = new TextBlock
        {
            Text = row.Description,
            Foreground = new SolidColorBrush(Color.FromRgb(0xB6, 0xC1, 0xD0)),
            TextWrapping = TextWrapping.Wrap,
        };

        Grid.SetColumn(term, 0);
        Grid.SetColumn(description, 1);
        grid.Children.Add(term);
        grid.Children.Add(description);
        return grid;
    }
}
