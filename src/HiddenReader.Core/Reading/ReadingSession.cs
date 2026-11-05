namespace HiddenReader.Core.Reading;

/// <summary>
/// 一本书的阅读会话：正文、样式、当前锚点（页首偏移）与搜索命中。
/// 目录、搜索与进度都以正文 UTF-16 偏移量表达，窗口重排或模式切换后
/// 通过 Paginator.FindPageStartContaining 定位回同一段文字。
/// </summary>
public sealed class ReadingSession
{
    public string Text { get; }
    public TextSettings Settings { get; set; } = new();

    /// <summary>当前页第一行的正文偏移量。</summary>
    public int Anchor { get; set; }

    /// <summary>全书搜索命中的起始偏移（升序）。</summary>
    public IReadOnlyList<int> SearchHits { get; private set; } = Array.Empty<int>();
    public int SearchHitLength { get; private set; }
    /// <summary>当前聚焦的命中序号，-1 表示无。</summary>
    public int ActiveSearchHit { get; set; } = -1;

    public ReadingSession(string text)
    {
        Text = text ?? string.Empty;
    }

    public void SetSearchHits(IReadOnlyList<int> hits, int length)
    {
        SearchHits = hits;
        SearchHitLength = length;
        ActiveSearchHit = hits.Count > 0 ? 0 : -1;
    }

    public void ClearSearch()
    {
        SearchHits = Array.Empty<int>();
        SearchHitLength = 0;
        ActiveSearchHit = -1;
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

    /// <summary>跳到包含指定偏移的页首（重排/模式切换后保持阅读位置）。</summary>
    public void JumpToOffset(int offset, double width, double height, double ppd)
    {
        Anchor = Paginator.FindPageStartContaining(Text, offset, width, height, Settings, ppd);
    }

    public void JumpToPage(int pageNumber, double width, double height, double ppd)
    {
        var starts = PageStarts(width, height, ppd);
        int index = Math.Clamp(pageNumber - 1, 0, starts.Count - 1);
        Anchor = starts[index];
    }

    public List<int> PageStarts(double width, double height, double ppd)
    {
        var starts = new List<int> { 0 };
        int start = 0;
        // 大书分页成本高：最多计算 4000 页，超出后按估算推进。
        for (int i = 0; i < 4000; i++)
        {
            var page = Paginator.Layout(Text, start, width, height, Settings, ppd);
            if (page.End <= start) break;
            start = page.End;
            if (start >= Text.Length) break;
            starts.Add(start);
        }
        return starts;
    }

    public int PageCount(double width, double height, double ppd) => PageStarts(width, height, ppd).Count;
}
