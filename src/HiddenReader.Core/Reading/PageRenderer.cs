using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HiddenReader.Core.Reading;

/// <summary>
/// 把分页结果绘制到 DrawingContext。普通窗口与浮窗共用同一渲染逻辑，
/// 保证两种模式的换行、页码与高亮完全一致。
/// </summary>
public static class PageRenderer
{
    // 默认暗黑主题：浅色文字 + 深色底。
    public static readonly Brush DefaultTextBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE4, 0xE2, 0xDC)));
    // 命中与选区高亮（UI-SPEC 1.1 tokens）：普通命中弱化、当前命中更亮、选区最亮，
    // 叠放顺序 selection > current > match。亮色自定义背景上蓝色叠加不可辨，
    // ResolveHighlightPalette 会改用正文色派生（审查缺陷 #4）。
    public static readonly Brush MatchHighlightBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xA3, 0xC8, 0xFF)));
    public static readonly Brush CurrentHighlightBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x6B, 0xA3, 0xC8, 0xFF)));
    public static readonly Brush SelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x80, 0x6F, 0xA8, 0xF5)));
    public static readonly HighlightPalette DefaultHighlights = new(MatchHighlightBrush, CurrentHighlightBrush, SelectionBrush);

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>一组高亮画刷：普通命中 / 当前命中 / 选区。</summary>
    public sealed record HighlightPalette(Brush Match, Brush Current, Brush Selection);

    // 缓存键必须同时包含背景与正文色：派生画刷取正文色，只按背景缓存会让
    // 「先把背景与正文设为同色、再只改正文色」拿到同色画刷（审查缺陷 #4）。
    private static readonly Dictionary<(Color Background, Color Text), HighlightPalette> HighlightCache = new();
    /// <summary>缓存条目上限：配色面板拖动滑杆会逐帧预览，避免缓存无限增长。</summary>
    private const int HighlightCacheLimit = 64;

    /// <summary>
    /// 按阅读背景与正文色解析高亮调色板。深色背景沿用默认蓝 token；亮色背景上蓝叠加几乎不可辨
    /// （如 #A3C8FF 背景，审查缺陷 #4），改用正文色派生：深色正文 → 深色高亮依然可辨。
    /// alpha 结构（0x33/0x6B/0x80）与叠放顺序 selection &gt; current &gt; match 保持不变。
    /// </summary>
    public static HighlightPalette ResolveHighlightPalette(Color background, Color textColor)
    {
        var key = (background, textColor);
        if (HighlightCache.TryGetValue(key, out var cached)) return cached;
        double luminance = (0.299 * background.R + 0.587 * background.G + 0.114 * background.B) / 255.0;
        var palette = DefaultHighlights;
        if (luminance >= 0.5)
        {
            var match = Freeze(new SolidColorBrush(Color.FromArgb(0x33, textColor.R, textColor.G, textColor.B)));
            var current = Freeze(new SolidColorBrush(Color.FromArgb(0x6B, textColor.R, textColor.G, textColor.B)));
            var selection = Freeze(new SolidColorBrush(Color.FromArgb(0x80, textColor.R, textColor.G, textColor.B)));
            palette = new HighlightPalette(match, current, selection);
        }
        if (HighlightCache.Count >= HighlightCacheLimit) HighlightCache.Clear();
        HighlightCache[key] = palette;
        return palette;
    }

    public static void DrawPage(
        DrawingContext dc,
        PageLayout page,
        Brush textBrush,
        IReadOnlyList<HighlightRange>? searchHighlights = null,
        HighlightRange? selection = null,
        HighlightPalette? highlights = null)
    {
        double padding = page.Settings.Padding;
        double lineHeight = page.Settings.LineHeight;
        var typeface = page.Settings.CreateTypeface();
        var palette = highlights ?? DefaultHighlights;

        if (searchHighlights is { Count: > 0 })
        {
            // 先画普通命中再画当前命中，保证当前命中盖在普通命中之上。
            foreach (var highlight in searchHighlights)
            {
                if (highlight.Kind == HighlightKind.Match)
                    DrawRangeBackground(dc, page, highlight, palette.Match, padding, lineHeight, typeface);
            }
            foreach (var highlight in searchHighlights)
            {
                if (highlight.Kind == HighlightKind.Current)
                    DrawRangeBackground(dc, page, highlight, palette.Current, padding, lineHeight, typeface);
            }
        }

        if (selection is { Length: > 0 } sel)
        {
            DrawRangeBackground(dc, page, sel, palette.Selection, padding, lineHeight, typeface);
        }

        foreach (var line in page.Lines)
        {
            if (line.IsEmpty) continue;
            var formatted = CreateText(line.Text, typeface, page.Settings, page.PixelsPerDip, textBrush);
            dc.DrawText(formatted, new Point(padding, line.Y));
        }
    }

    private static void DrawRangeBackground(
        DrawingContext dc,
        PageLayout page,
        HighlightRange range,
        Brush brush,
        double padding,
        double lineHeight,
        Typeface typeface)
    {
        int rangeEnd = range.Start + range.Length;
        foreach (var line in page.Lines)
        {
            if (line.IsEmpty) continue;
            int lineEnd = line.Start + line.Length;
            if (lineEnd <= range.Start || line.Start >= rangeEnd) continue;

            int localStart = Math.Max(0, range.Start - line.Start);
            int localEnd = Math.Min(line.Length, rangeEnd - line.Start);
            if (localEnd <= localStart) continue;

            double x1 = padding + Paginator.Measure(line.Text[..localStart], typeface, page.Settings, page.PixelsPerDip);
            double x2 = padding + Paginator.Measure(line.Text[..localEnd], typeface, page.Settings, page.PixelsPerDip);
            var rect = new Rect(x1, line.Y, Math.Max(2, x2 - x1), lineHeight * 0.98);
            dc.DrawRectangle(brush, null, rect);
        }
    }

    private static FormattedText CreateText(string text, Typeface typeface, TextSettings settings, double ppd, Brush brush)
    {
        return new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            settings.FontSize,
            brush,
            ppd)
        {
            Trimming = TextTrimming.None,
        };
    }

    /// <summary>把页面内的点（DIP，相对控件左上角）换算为正文偏移。</summary>
    public static int HitTest(PageLayout page, Point point, bool clampToPage = true)
    {
        if (page.Lines.Count == 0) return page.Start;

        var line = page.Lines[0];
        foreach (var candidate in page.Lines)
        {
            if (point.Y >= candidate.Y && point.Y < candidate.Y + page.Settings.LineHeight) line = candidate;
        }

        if (point.Y >= page.Lines[^1].Y + page.Settings.LineHeight)
        {
            return clampToPage ? page.End : page.Lines[^1].Start + page.Lines[^1].Length;
        }

        if (line.IsEmpty) return line.Start;

        double padding = page.Settings.Padding;
        var typeface = page.Settings.CreateTypeface();
        double x = Math.Max(0, point.X - padding);

        // 二分查找光标所在的字符边界。
        int lo = 0, hi = line.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Paginator.Measure(line.Text[..mid], typeface, page.Settings, page.PixelsPerDip) <= x) lo = mid;
            else hi = mid - 1;
        }

        return line.Start + lo;
    }

    /// <summary>返回给定正文偏移在页面中的矩形（用于调试与自动化校验）。</summary>
    public static Rect GetRect(PageLayout page, int offset)
    {
        double padding = page.Settings.Padding;
        double lineHeight = page.Settings.LineHeight;
        var typeface = page.Settings.CreateTypeface();
        foreach (var line in page.Lines)
        {
            if (offset >= line.Start && offset <= line.Start + line.Length)
            {
                int local = Math.Clamp(offset - line.Start, 0, line.Length);
                double x = padding + Paginator.Measure(line.Text[..local], typeface, page.Settings, page.PixelsPerDip);
                return new Rect(x, line.Y, 4, lineHeight * 0.9);
            }
        }
        return Rect.Empty;
    }
}
