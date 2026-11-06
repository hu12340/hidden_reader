using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using HiddenReader.App.Data;

namespace HiddenReader.App.UI;

/// <summary>
/// 应用级阅读配色：已保存值（Confirmed）与面板草稿分离。
/// 视图只读 <see cref="Text"/>/<see cref="Background"/>（预览时是草稿，其余时间是已确认值），
/// 因此预览不会写进 <see cref="AppState"/>，锚点、窗口等其他保存也不会把草稿落盘。
/// </summary>
internal static class ReadingColors
{
    public const string DefaultTextHex = "#E4E2DC";
    public const string DefaultBackgroundHex = "#000000";

    public static readonly Color DefaultText = Color.FromRgb(0xE4, 0xE2, 0xDC);
    public static readonly Color DefaultBackground = Colors.Black;

    /// <summary>已确认（已保存）的配色：取消预览或保存失败时回到这里。</summary>
    public static Color ConfirmedText { get; private set; } = DefaultText;
    public static Color ConfirmedBackground { get; private set; } = DefaultBackground;

    /// <summary>当前用于绘制的配色。</summary>
    public static Color Text { get; private set; } = DefaultText;
    public static Color Background { get; private set; } = DefaultBackground;

    /// <summary>配色变化：两个阅读视图只重绘当前页，不重排。</summary>
    public static event Action? Changed;

    /// <summary>启动时从应用状态载入；缺少或无法解析的字段使用默认值（旧状态兼容）。</summary>
    public static void Load(AppState state)
    {
        ConfirmedText = TryParse(state.TextColor, out var text) ? text : DefaultText;
        ConfirmedBackground = TryParse(state.BackgroundColor, out var background) ? background : DefaultBackground;
        Apply(ConfirmedText, ConfirmedBackground);
    }

    /// <summary>面板草稿预览：只影响绘制。</summary>
    public static void Preview(Color text, Color background) => Apply(text, background);

    /// <summary>确定保存成功后把草稿转为已确认值。</summary>
    public static void Confirm(Color text, Color background)
    {
        ConfirmedText = text;
        ConfirmedBackground = background;
        Apply(text, background);
    }

    /// <summary>取消或保存失败：视图回到已确认值。</summary>
    public static void RestoreConfirmed() => Apply(ConfirmedText, ConfirmedBackground);

    private static void Apply(Color text, Color background)
    {
        if (text == Text && background == Background) return;
        Text = text;
        Background = background;
        Changed?.Invoke();
    }

    /// <summary>解析 `#RRGGBB`（也接受不带 # 的六位十六进制）。</summary>
    public static bool TryParse(string? value, out Color color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        string text = value.Trim();
        if (text.StartsWith('#')) text = text[1..];
        if (text.Length != 6) return false;
        if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return false;
        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    public static string Format(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}

/// <summary>
/// 阅读配色面板：普通窗口工具栏与极简浮窗右键菜单共用的同一个面板。
/// 编辑时把草稿交给宿主预览（只重绘当前页），「取消」恢复打开面板前的配色，
/// 「确定」交给宿主沿用 Library.Save() 原子保存。
/// </summary>
public partial class ReadingColorsWindow : Window
{
    private readonly Action<Color, Color> _preview;
    private readonly Func<Color, Color, bool> _commit;
    private readonly Action _cancel;
    private Color _textDraft;
    private Color _backgroundDraft;
    /// <summary>程序改写色值框期间不回灌输入事件，避免把规范化的文本当成用户编辑。</summary>
    private bool _updatingBoxes;
    private bool _confirmed;

    internal ReadingColorsWindow(Color text, Color background,
        Action<Color, Color> preview, Func<Color, Color, bool> commit, Action cancel)
    {
        _preview = preview;
        _commit = commit;
        _cancel = cancel;
        InitializeComponent();
        // 与普通窗口一致的深色标题栏（系统浅色主题下也保持深色）。
        DarkTitleBar.Attach(this);
        SetDrafts(text, background, preview: false);
        PreviewKeyDown += OnPreviewKeyDown;
    }

    /// <summary>写入草稿并同步色块与色值框；refreshBoxes 为假时保留用户正在编辑的文本。</summary>
    private void SetDrafts(Color text, Color background, bool preview = true)
    {
        _textDraft = text;
        _backgroundDraft = background;
        _updatingBoxes = true;
        try
        {
            TextHexBox.Text = ReadingColors.Format(text);
            BackgroundHexBox.Text = ReadingColors.Format(background);
        }
        finally
        {
            _updatingBoxes = false;
        }
        UpdateSwatches();
        if (preview) _preview(text, background);
    }

    private void UpdateSwatches()
    {
        TextSwatch.Background = new SolidColorBrush(_textDraft);
        BackgroundSwatch.Background = new SolidColorBrush(_backgroundDraft);
    }

    /// <summary>色值框逐字符输入：只有两项都仍是合法 #RRGGBB 时才更新草稿与预览。</summary>
    private void OnTextHexChanged(object sender, TextChangedEventArgs e)
    {
        if (_updatingBoxes) return;
        if (!ReadingColors.TryParse(TextHexBox.Text, out var text)) return;
        if (!ReadingColors.TryParse(BackgroundHexBox.Text, out var background)) return;
        SetDrafts(text, background);
    }

    /// <summary>失焦时规范化输入；无法解析则回到草稿值（草稿始终是最后一次生效的颜色）。</summary>
    private void OnHexLostFocus(object sender, RoutedEventArgs e)
    {
        if (_updatingBoxes) return;
        bool isText = ReferenceEquals(sender, TextHexBox);
        var box = isText ? TextHexBox : BackgroundHexBox;
        Color draft = isText ? _textDraft : _backgroundDraft;
        Color shown = ReadingColors.TryParse(box.Text, out var parsed) ? parsed : draft;
        _updatingBoxes = true;
        try
        {
            box.Text = ReadingColors.Format(shown);
        }
        finally
        {
            _updatingBoxes = false;
        }
    }

    private void OnTextSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (NativeColorDialog.TryPick(this, _textDraft, out var picked))
            SetDrafts(picked, _backgroundDraft);
    }

    private void OnBackgroundSwatchClick(object sender, MouseButtonEventArgs e)
    {
        if (NativeColorDialog.TryPick(this, _backgroundDraft, out var picked))
            SetDrafts(_textDraft, picked);
    }

    private void OnRestoreDefaults(object sender, RoutedEventArgs e)
        => SetDrafts(ReadingColors.DefaultText, ReadingColors.DefaultBackground);

    private void OnPickText(object sender, RoutedEventArgs e) => Pick(background: false);

    private void OnPickBackground(object sender, RoutedEventArgs e) => Pick(background: true);

    /// <summary>
    /// 取色时暂时收起面板：取色窗口需要看到完整桌面，取色点击也不能落回面板。
    /// 取色成功填回发起取色的那一行；Esc 取消时保留取色前的草稿与当前阅读模式。
    /// 无论成功、取消还是异常，都在 finally 里恢复面板。
    /// </summary>
    private void Pick(bool background)
    {
        Hide();
        Color? picked = null;
        try
        {
            picked = ScreenColorPicker.Pick();
        }
        catch (Exception ex)
        {
            Diagnostics.Log("screen picker failed: " + ex);
            MessageBox.Show(this, "取色失败：" + ex.Message, "HiddenReader",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Show();
            Activate();
            Focus();
        }

        if (picked is not Color color) return; // Esc：保留取色前的草稿
        if (background) SetDrafts(_textDraft, color);
        else SetDrafts(color, _backgroundDraft);
    }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        // 以最后一次可解析的输入为准；无法解析时沿用草稿。
        Color text = ReadingColors.TryParse(TextHexBox.Text, out var parsedText) ? parsedText : _textDraft;
        Color background = ReadingColors.TryParse(BackgroundHexBox.Text, out var parsedBackground)
            ? parsedBackground
            : _backgroundDraft;

        if (_commit(text, background))
        {
            _confirmed = true;
            Close();
            return;
        }

        // 保存失败：宿主已把视图与状态恢复为已确认值，草稿仍留在面板里供重试。
        MessageBox.Show(this,
            "阅读配色保存失败，已恢复上次保存的配色。\n面板保留当前草稿，可检查磁盘后重试。",
            "HiddenReader", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        // Esc 等同「取消」：恢复打开面板前的配色。
        Close();
        e.Handled = true;
    }

    protected override void OnClosed(EventArgs e)
    {
        // 关闭按钮与 Esc 同「取消」；确定成功后不再恢复。
        if (!_confirmed) _cancel();
        base.OnClosed(e);
    }
}

/// <summary>点击色块时打开系统颜色对话框，返回选中的颜色（取消则不改动草稿）。</summary>
internal static class NativeColorDialog
{
    private const int CcRgbInit = 0x00000001;
    private const int CcFullOpen = 0x00000002;

    // 自定义颜色槽在对话框生命周期内保留，静态数组让二次打开时仍在原位。
    private static readonly int[] CustomColors = new int[16];

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ChooseColorData
    {
        public int Size;
        public IntPtr Owner;
        public IntPtr Instance;
        public int Result;
        public IntPtr CustomColors;
        public int Flags;
        public IntPtr CustomData;
        public IntPtr Hook;
        public string? TemplateName;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ChooseColorW(ref ChooseColorData data);

    public static bool TryPick(Window owner, Color initial, out Color picked)
    {
        picked = initial;
        IntPtr ownerHandle = new System.Windows.Interop.WindowInteropHelper(owner).Handle;
        IntPtr colors = Marshal.AllocHGlobal(sizeof(int) * CustomColors.Length);
        try
        {
            Marshal.Copy(CustomColors, 0, colors, CustomColors.Length);
            var data = new ChooseColorData
            {
                Size = Marshal.SizeOf<ChooseColorData>(),
                Owner = ownerHandle,
                Result = ToColorRef(initial),
                CustomColors = colors,
                Flags = CcRgbInit | CcFullOpen,
            };
            if (!ChooseColorW(ref data)) return false;
            picked = FromColorRef(data.Result);
            Marshal.Copy(colors, CustomColors, 0, CustomColors.Length);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(colors);
        }
    }

    /// <summary>COLORREF 为 0x00BBGGRR，与 WPF 的 RGB 顺序相反。</summary>
    private static int ToColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    private static Color FromColorRef(int value)
        => Color.FromRgb((byte)(value & 0xFF), (byte)((value >> 8) & 0xFF), (byte)((value >> 16) & 0xFF));
}
