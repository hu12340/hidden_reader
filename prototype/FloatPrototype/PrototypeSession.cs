using HiddenReader.Core.Reading;

namespace FloatPrototype;

/// <summary>原型共用状态：两页固定正文与当前阅读锚点。</summary>
public sealed class PrototypeSession
{
    public string Text { get; } = SampleText.Content;

    public TextSettings Settings { get; } = new()
    {
        FontFamily = "Microsoft YaHei UI",
        FontSize = 18,
        LineHeightFactor = 1.85,
        Padding = 16,
    };

    /// <summary>当前页第一行的正文偏移量。</summary>
    public int Anchor { get; set; }

    public int PageCount(double width, double height, double ppd) => PageStarts(width, height, ppd).Count;

    public int PageNumber(double width, double height, double ppd)
    {
        var starts = PageStarts(width, height, ppd);
        int index = starts.IndexOf(Anchor);
        return index >= 0 ? index + 1 : 1;
    }

    public void NextPage(double width, double height, double ppd)
    {
        var page = Paginator.Layout(Text, Anchor, width, height, Settings, ppd);
        Anchor = page.End > Anchor ? page.End : Math.Min(Text.Length, Anchor + 1);
    }

    public void PreviousPage(double width, double height, double ppd)
    {
        Anchor = Paginator.PreviousPageStart(Text, Anchor, width, height, Settings, ppd);
    }

    public List<int> PageStarts(double width, double height, double ppd)
    {
        var starts = new List<int> { 0 };
        int start = 0;
        for (int i = 0; i < 50; i++)
        {
            var page = Paginator.Layout(Text, start, width, height, Settings, ppd);
            if (page.End <= start) break;
            start = page.End;
            if (start >= Text.Length) break;
            starts.Add(start);
        }
        return starts;
    }
}
