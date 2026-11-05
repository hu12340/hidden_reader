using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace HiddenReader.Core.Reading;

public sealed class LayoutLine
{
    public int Start { get; init; }
    public int Length { get; init; }
    public string Text { get; init; } = string.Empty;
    public double Y { get; init; }
    public double Width { get; init; }
    public bool IsEmpty => Length == 0;
}

public sealed class PageLayout
{
    public int Start { get; set; }
    /// <summary>本页最后一行结束后的正文偏移（不含被消费的换行符）。</summary>
    public int End { get; set; }
    public List<LayoutLine> Lines { get; init; } = new();
    public double ContentWidth { get; init; }
    public double ContentHeight { get; init; }
    public TextSettings Settings { get; init; } = new();
    public double PixelsPerDip { get; init; }
}

public sealed record HighlightRange(int Start, int Length);

/// <summary>
/// 按可用宽高把连续正文切分为页面。行与页边界都以正文 UTF-16 偏移量表达，
/// 因此缩放、模式切换与重启后都能定位回同一段文字。
/// </summary>
public static class Paginator
{
    private const string LeadingForbidden = "，。、；：？！）】》」』”’%,.;:?!)]}";
    private const string TrailingForbidden = "（【《「『“‘([{";

    public static PageLayout Layout(string text, int start, double viewWidth, double viewHeight, TextSettings settings, double pixelsPerDip)
    {
        start = Math.Clamp(start, 0, Math.Max(0, text.Length));
        var page = new PageLayout
        {
            Start = start,
            ContentWidth = viewWidth,
            ContentHeight = viewHeight,
            Settings = settings,
            PixelsPerDip = pixelsPerDip,
        };

        double padding = settings.Padding;
        double maxWidth = Math.Max(8, viewWidth - padding * 2);
        double maxBottom = viewHeight - padding;
        double lineHeight = settings.LineHeight;
        var typeface = settings.CreateTypeface();

        int pos = start;
        double y = padding;

        while (pos < text.Length && y + lineHeight <= maxBottom + 0.01)
        {
            if (text[pos] == '\n')
            {
                page.Lines.Add(new LayoutLine { Start = pos, Length = 0, Text = string.Empty, Y = y, Width = 0 });
                pos++;
                y += lineHeight;
                continue;
            }

            int paraEnd = text.IndexOf('\n', pos);
            if (paraEnd < 0) paraEnd = text.Length;

            int end = FindLineBreak(text, pos, paraEnd, maxWidth, typeface, settings, pixelsPerDip);
            var lineText = text[pos..end];
            double width = Measure(lineText, typeface, settings, pixelsPerDip);
            page.Lines.Add(new LayoutLine { Start = pos, Length = end - pos, Text = lineText, Y = y, Width = width });

            pos = end;
            if (pos < text.Length && text[pos] == '\n') pos++;
            y += lineHeight;
        }

        page.End = page.Lines.Count > 0
            ? page.Lines[^1].Start + page.Lines[^1].Length
            : start;
        return page;
    }

    private static int FindLineBreak(string text, int start, int paraEnd, double maxWidth, Typeface typeface, TextSettings settings, double ppd)
    {
        int length = paraEnd - start;
        if (length <= 0) return start;

        // 用一段样本估算每行容量，随后在容量附近二分，避免对长段落逐字测量。
        int sampleLen = Math.Min(length, 24);
        double sampleWidth = Measure(text.Substring(start, sampleLen), typeface, settings, ppd);
        double avg = sampleLen > 0 ? Math.Max(1, sampleWidth / sampleLen) : settings.FontSize;
        int estimate = Math.Clamp((int)(maxWidth / avg) + 1, 1, length);

        int lo = Math.Max(1, estimate - 12);
        int hi = Math.Min(length, estimate + 12);
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Measure(text.Substring(start, mid), typeface, settings, ppd) <= maxWidth + 0.5) lo = mid;
            else hi = mid - 1;
        }

        int end = start + Math.Max(1, lo);

        // 避免标点出现在行首：把下一行开头的标点收进当前行。
        while (end < paraEnd && LeadingForbidden.IndexOf(text[end]) >= 0)
        {
            end++;
        }

        // 避免行尾留下开引号或开括号。
        while (end > start + 1 && TrailingForbidden.IndexOf(text[end - 1]) >= 0)
        {
            end--;
        }

        return end;
    }

    public static double Measure(string text, Typeface typeface, TextSettings settings, double ppd)
    {
        if (text.Length == 0) return 0;
        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            settings.FontSize,
            Brushes.Black,
            ppd)
        {
            Trimming = TextTrimming.None,
        };
        return formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>找到包含指定偏移的页起始位置（用于窗口尺寸变化后保持阅读位置）。</summary>
    public static int FindPageStartContaining(string text, int offset, double width, double height, TextSettings settings, double ppd)
    {
        int target = Math.Clamp(offset, 0, Math.Max(0, text.Length));
        const int maxBack = 6000;
        int start = Math.Max(0, target - maxBack);
        for (int i = 0; i < 400; i++)
        {
            var page = Layout(text, start, width, height, settings, ppd);
            if (page.End >= target || page.Lines.Count == 0) return start;
            if (page.End <= start) return start;
            start = page.End;
        }
        return start;
    }

    /// <summary>返回当前页之前一页的起始偏移。</summary>
    public static int PreviousPageStart(string text, int currentStart, double width, double height, TextSettings settings, double ppd)
    {
        if (currentStart <= 0) return 0;
        const int maxBack = 6000;
        int start = Math.Max(0, currentStart - maxBack);
        int previous = start;
        for (int i = 0; i < 400; i++)
        {
            if (start >= currentStart) return previous;
            var page = Layout(text, start, width, height, settings, ppd);
            if (page.End >= currentStart) return start;
            if (page.End <= start) return previous;
            previous = start;
            start = page.End;
        }
        return previous;
    }
}
