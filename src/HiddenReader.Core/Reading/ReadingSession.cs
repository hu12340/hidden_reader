namespace HiddenReader.Core.Reading;

/// <summary>
/// 一本书的阅读会话：正文、样式、当前锚点（可视区首行的正文偏移）与搜索命中。
/// 目录、搜索与进度都以正文 UTF-16 偏移量表达，窗口重排或模式切换后
/// 通过 Paginator.FindPageStartContaining 定位回同一段文字。
/// 锚点既可能是页首，也可能是滚动后停在两页之间的行首；翻页一律
/// 从当前可视位置排版一屏推进（NextPage），或回退一屏行数（PreviousPage）。
/// </summary>
public sealed class ReadingSession
{
    public string Text { get; }
    public TextSettings Settings { get; set; } = new();

    /// <summary>当前可视区第一行的正文偏移量。</summary>
    public int Anchor { get; set; }

    /// <summary>全书搜索命中的起始偏移（升序）。</summary>
    public IReadOnlyList<int> SearchHits { get; private set; } = Array.Empty<int>();
    public int SearchHitLength { get; private set; }
    /// <summary>当前聚焦的命中序号，-1 表示无。</summary>
    public int ActiveSearchHit { get; set; } = -1;

    // 全书分页结果缓存：由后台分页线程（大书）或按需同步计算（小书）填充。
    // 键为产生该结果的布局尺寸，窗口缩放后由调用方重新请求。
    private IReadOnlyList<int>? _cachedStarts;
    private (double W, double H, double Ppd)? _cachedKey;

    public ReadingSession(string text)
    {
        Text = text ?? string.Empty;
    }

    /// <summary>后台分页完成后写入结果；键匹配时返回准确的页码与总页数。</summary>
    public void SetPageStarts(IReadOnlyList<int> starts, double width, double height, double ppd)
    {
        _cachedStarts = starts;
        _cachedKey = (width, height, ppd);
    }

    /// <summary>当前布局的总页数是否已经算好。</summary>
    public bool HasPageStarts(double width, double height, double ppd)
        => _cachedStarts is not null && _cachedKey == (width, height, ppd);

    /// <summary>已算好的总页数；未算好时返回 0（界面此时显示阅读百分比）。</summary>
    public int CachedPageCount => _cachedStarts?.Count ?? 0;

    /// <summary>
    /// 锚点所在页码（1 起）：最后一个不大于锚点的页首。
    /// 锚点可能停在两页之间，因此取包含它的页。总页数未算好时返回 0。
    /// </summary>
    public int CachedPageIndex(int anchor)
    {
        var starts = _cachedStarts;
        if (starts is null || starts.Count == 0) return 0;
        // 锚点单调递增地对照页首；线性扫描足够（页数量级为数千）。
        int page = 0;
        for (int i = 0; i < starts.Count; i++)
        {
            if (starts[i] <= anchor) page = i + 1;
            else break;
        }
        return page;
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

    /// <summary>向后翻一屏：从当前可视位置排版一整屏，锚点移到其后。</summary>
    public void NextPage(double width, double height, double ppd)
    {
        var page = Paginator.Layout(Text, Anchor, width, height, Settings, ppd);
        Anchor = page.End > Anchor ? page.End : Math.Min(Text.Length, Anchor + 1);
    }

    /// <summary>向前翻一屏：回退约一屏的行数，使刚离开视野的前文整屏出现。</summary>
    public void PreviousPage(double width, double height, double ppd)
    {
        var page = Paginator.Layout(Text, Anchor, width, height, Settings, ppd);
        int lines = Math.Max(1, page.Lines.Count);
        var starts = Paginator.CollectLineStartsBefore(Text, Anchor, lines, width, height, Settings, ppd);
        Anchor = starts.Count > 0 ? starts[^1] : 0;
    }

    /// <summary>滚轮向下：连续移动行首，可停在整页边界之间。已显示最后一屏时不再移动。</summary>
    public bool ScrollDownLines(int lines, double width, double height, double ppd)
    {
        if (lines <= 0) return false;
        int top = Anchor;
        bool moved = false;
        for (int i = 0; i < lines; i++)
        {
            var page = Paginator.Layout(Text, top, width, height, Settings, ppd);
            if (page.Lines.Count == 0) break;
            // 当前屏已显示到正文末尾（其后只剩空白）时到底，避免滚出空白页。
            if (Text.AsSpan(page.End).IsWhiteSpace()) break;
            int? next = null;
            foreach (var line in page.Lines)
            {
                if (line.Start > top) { next = line.Start; break; }
            }
            if (next is int lineStart)
            {
                top = lineStart;
                moved = true;
            }
            else if (page.End > top)
            {
                // 下一行在下一页：页首（page.End）就是下一行的行首。
                top = page.End;
                moved = true;
            }
            else break;
        }
        if (moved) Anchor = top;
        return moved;
    }

    /// <summary>滚轮向上：连续移动行首。已在文本开头时不再移动。</summary>
    public bool ScrollUpLines(int lines, double width, double height, double ppd)
    {
        if (lines <= 0 || Anchor <= 0) return false;
        var starts = Paginator.CollectLineStartsBefore(Text, Anchor, lines, width, height, Settings, ppd);
        if (starts.Count == 0) return false;
        Anchor = starts[^1];
        return true;
    }

    /// <summary>跳到包含指定偏移的页首（重排/模式切换后保持阅读位置）。</summary>
    public void JumpToOffset(int offset, double width, double height, double ppd)
    {
        Anchor = Paginator.FindPageStartContaining(Text, offset, width, height, Settings, ppd);
    }

    public void JumpToPage(int pageNumber, double width, double height, double ppd)
    {
        var starts = GetPageStarts(width, height, ppd);
        if (starts.Count == 0) return;
        int index = Math.Clamp(pageNumber - 1, 0, starts.Count - 1);
        Anchor = starts[index];
    }

    /// <summary>
    /// 页首列表：优先用后台分页缓存；未命中时同步计算（仅小书可承受，
    /// 大书调用方应先等待 HasPageStarts）。
    /// </summary>
    public IReadOnlyList<int> GetPageStarts(double width, double height, double ppd)
    {
        if (HasPageStarts(width, height, ppd)) return _cachedStarts!;
        var starts = ComputePageStarts(width, height, ppd);
        SetPageStarts(starts, width, height, ppd);
        return starts;
    }

    public List<int> PageStarts(double width, double height, double ppd) => ComputePageStarts(width, height, ppd);

    private List<int> ComputePageStarts(double width, double height, double ppd)
    {
        var starts = new List<int> { 0 };
        int start = 0;
        // 逐页排版直至文末；Page.End 不前进说明排版异常，终止以防死循环。
        while (start < Text.Length)
        {
            var page = Paginator.Layout(Text, start, width, height, Settings, ppd);
            if (page.End <= start) break;
            start = page.End;
            if (start >= Text.Length) break;
            starts.Add(start);
        }
        return starts;
    }

    public int PageCount(double width, double height, double ppd) => ComputePageStarts(width, height, ppd).Count;
}
