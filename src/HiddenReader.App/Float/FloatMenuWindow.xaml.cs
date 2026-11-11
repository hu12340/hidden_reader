using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HiddenReader.App.UI;

namespace HiddenReader.App.Float;

/// <summary>浮窗菜单行种类：命令、分组标签、分隔线。</summary>
public enum FloatMenuRowKind
{
    Command,
    GroupLabel,
    Separator,
}

/// <summary>浮窗菜单行模型：命令（含勾选态、行尾后缀、危险色）或分组标签/分隔线。</summary>
public sealed class FloatMenuRow
{
    public FloatMenuRow(FloatMenuRowKind kind, int id = 0, string text = "",
        string? suffix = null, bool accentSuffix = false, bool @checked = false, bool danger = false)
    {
        Kind = kind;
        Id = id;
        Text = text;
        Suffix = suffix;
        AccentSuffix = accentSuffix;
        Checked = @checked;
        Danger = danger;
    }

    public FloatMenuRowKind Kind { get; }
    public int Id { get; }
    public string Text { get; }
    public string? Suffix { get; }
    /// <summary>行尾后缀着色：真 = 实时数值（accent.text 11px），假 = 快捷键提示（10px）。</summary>
    public bool AccentSuffix { get; }
    public bool Checked { get; }
    public bool Danger { get; }
    public bool IsCommand => Kind == FloatMenuRowKind.Command;
    public bool IsGroupLabel => Kind == FloatMenuRowKind.GroupLabel;
    public bool IsSeparator => Kind == FloatMenuRowKind.Separator;

    /// <summary>估算行高（DIP）：命令 32、组标签 16+边距 6、分隔线 1+边距 10。</summary>
    public int EstimatedHeight => Kind switch
    {
        FloatMenuRowKind.Command => 32,
        FloatMenuRowKind.GroupLabel => 22,
        _ => 11,
    };
}

/// <summary>
/// 浮窗右键菜单窗口（UI-11+UI-12）：深色自绘浮层，行为补齐 Win32 模态语义——
/// 外击（含点击浮窗本体）关闭且不触发翻页；Esc 只关菜单；命令执行/菜单关闭后
/// 由宿主把焦点交回浮窗。任意系统主题下保持深色可读。
/// </summary>
public partial class FloatMenuWindow : Window
{
    /// <summary>菜单命令激活（已先关闭菜单）：参数为命令 id。</summary>
    public event Action<int>? ItemActivated;

    private readonly List<Button> _commands = new();
    /// <summary>因失活关闭时为真：宿主不回送焦点（用户已切去别的应用）。</summary>
    public bool ClosedByDeactivation => _closedByDeactivation;
    private bool _closedByDeactivation;
    /// <summary>关闭流程已开始：关闭期间仍会收到 Deactivated，重复 Close 会抛 InvalidOperationException。</summary>
    private bool _closeStarted;

    public FloatMenuWindow(IReadOnlyList<FloatMenuRow> rows, double ownerScale, bool topmost,
        IntPtr ownerHandle, double leftDip, double topDip)
    {
        InitializeComponent();
        // 浮窗可能在置顶带：菜单随宿主置顶，保证置顶浮窗的菜单仍在前台。
        Topmost = topmost;
        new System.Windows.Interop.WindowInteropHelper(this) { Owner = ownerHandle };
        // leftDip/topDip 为含阴影边距的整窗左上角（宿主已按估算尺寸夹回工作区，物理像素转 DIP）。
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = leftDip;
        Top = topDip;

        BuildItems(rows);
        // 首次渲染后按实际尺寸和目标显示器工作区做最终包含校正：
        // 宿主侧预夹回用的是估算尺寸，真实高度可能更大而越出右/下边缘（审查缺陷 #6）。
        ContentRendered += (_, _) =>
        {
            var physical = new WindowPlacement.RECT
            {
                Left = (int)Math.Round(Left * ownerScale),
                Top = (int)Math.Round(Top * ownerScale),
                Right = (int)Math.Round((Left + ActualWidth) * ownerScale),
                Bottom = (int)Math.Round((Top + ActualHeight) * ownerScale),
            };
            if (WindowPlacement.TryGetWorkAreaForRect(physical, out var workArea)
                && !WindowPlacement.IsRectContainedInWorkArea(physical, workArea))
            {
                var fixedRect = WindowPlacement.ClampIntoWorkArea(physical, workArea);
                Left = fixedRect.Left / ownerScale;
                Top = fixedRect.Top / ownerScale;
            }
        };
        Deactivated += (_, _) =>
        {
            if (_closeStarted) return; // 关闭期间失活：重复 Close 会抛异常
            _closeStarted = true;
            _closedByDeactivation = true;
            Close();
        };
        PreviewKeyDown += OnMenuKeyDown;
    }

    /// <summary>含阴影边距后的整窗估算宽度（DIP）：内容 220 + 两侧各 16。</summary>
    public static double EstimatedWidth => 252;

    /// <summary>整窗估算高度（DIP）：内容行合计 + 上下内距 10 + 两侧阴影边距 32。</summary>
    public static double EstimateHeight(IReadOnlyList<FloatMenuRow> rows)
        => rows.Sum(r => r.EstimatedHeight) + 10 + 32;

    private void BuildItems(IReadOnlyList<FloatMenuRow> rows)
    {
        foreach (var row in rows)
        {
            if (row.IsGroupLabel)
            {
                ItemsHost.Children.Add(new TextBlock
                {
                    Text = row.Text,
                    Style = (Style)Resources["FloatGroupLabel"],
                });
                continue;
            }
            if (row.IsSeparator)
            {
                ItemsHost.Children.Add(new Border
                {
                    Background = new SolidColorBrush(Color.FromRgb(0x3A, 0x43, 0x51)),
                    Height = 1,
                    Margin = new Thickness(0, 5, 0, 5),
                });
                continue;
            }

            var button = new Button
            {
                Style = (Style)Resources["FloatMenuCommand"],
                Tag = row.Id,
            };
            var content = new Grid();
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Auto) });

            var labelPanel = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            if (row.Checked)
            {
                // 勾选态：行首 10×10 accent.text 方点（语义与原 MF_CHECKED 一致）。
                labelPanel.Children.Add(new Border
                {
                    Width = 10,
                    Height = 10,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Color.FromRgb(0x9B, 0xC4, 0xFF)),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }
            var labelText = new TextBlock
            {
                Text = row.Text,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = row.Danger
                    ? new SolidColorBrush(Color.FromRgb(0xC9, 0xA0, 0xA6))
                    : new SolidColorBrush(Color.FromRgb(0xE5, 0xE8, 0xEE)),
            };
            labelPanel.Children.Add(labelText);
            Grid.SetColumn(labelPanel, 0);
            content.Children.Add(labelPanel);

            if (row.Suffix is not null)
            {
                var suffix = new TextBlock
                {
                    Text = row.Suffix,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0),
                };
                if (row.AccentSuffix)
                {
                    // 行尾实时数值：11px accent.text，等宽现状。
                    suffix.FontSize = 11;
                    suffix.FontFamily = new FontFamily("Consolas");
                    suffix.Foreground = new SolidColorBrush(Color.FromRgb(0x9B, 0xC4, 0xFF));
                }
                else
                {
                    // 快捷键提示（如 Esc）：10px 次要色。
                    suffix.FontSize = 10;
                    suffix.Foreground = new SolidColorBrush(Color.FromRgb(0x8A, 0x94, 0xA3));
                }
                Grid.SetColumn(suffix, 1);
                content.Children.Add(suffix);
            }

            button.Content = content;
            button.Click += OnItemClick;
            _commands.Add(button);
            ItemsHost.Children.Add(button);
        }
    }

    private void OnItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: int id }) return;
        if (_closeStarted) return;
        // 先关菜单再执行命令：Closed 里回送焦点后，命令打开的面板仍会自己抢到前台。
        _closeStarted = true;
        Close();
        ItemActivated?.Invoke(id);
    }

    private void OnMenuKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (!_closeStarted)
                {
                    _closeStarted = true;
                    Close();
                }
                e.Handled = true;
                break;
            case Key.Up:
            case Key.Down:
                MoveFocus(e.Key == Key.Up ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Enter:
                if (Keyboard.FocusedElement is Button button)
                {
                    button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
                }
                e.Handled = true;
                break;
        }
    }

    private void MoveFocus(int delta)
    {
        if (_commands.Count == 0) return;
        int index = _commands.FindIndex(b => ReferenceEquals(Keyboard.FocusedElement, b));
        if (index < 0) index = 0;
        else index = (index + delta + _commands.Count) % _commands.Count;
        _commands[index].Focus();
    }

    /// <summary>打开后焦点默认落在第一条命令（键盘可直接 ↑↓/Enter 操作）。</summary>
    public void FocusFirstItem()
    {
        if (_commands.Count > 0) _commands[0].Focus();
    }
}
