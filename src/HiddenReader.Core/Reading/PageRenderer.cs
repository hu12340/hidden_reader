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
    public static readonly Brush SearchHighlightBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x73, 0xE8, 0xA3, 0x3D)));
    public static readonly Brush SelectionBrush = Freeze(new SolidColorBrush(Color.FromArgb(0x59, 0x3D, 0x7B, 0xE8)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    public static void DrawPage(
        DrawingContext dc,
        PageLayout page,
        Brush textBrush,
        IReadOnlyList<HighlightRange>? searchHighlights = null,
        HighlightRange? selection = null)
    {
        double padding = page.Settings.Padding;
        double lineHeight = page.Settings.LineHeight;
        var typeface = page.Settings.CreateTypeface();

        if (searchHighlights is { Count: > 0 })
        {
            foreach (var highlight in searchHighlights)
            {
                DrawRangeBackground(dc, page, highlight, SearchHighlightBrush, padding, lineHeight, typeface);
            }
        }

        if (selection is { Length: > 0 } sel)
        {
            DrawRangeBackground(dc, page, sel, SelectionBrush, padding, lineHeight, typeface);
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
