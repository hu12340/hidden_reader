using System.Text;
using System.Text.RegularExpressions;

namespace HiddenReader.Core.Reading;

/// <summary>全书搜索：返回所有命中的起始偏移（升序），忽略大小写。</summary>
public static class BookSearch
{
    /// <summary>
    /// 单次正则扫描的字符窗口。正则一次扫完整篇时，无命中或首个命中很晚的正文中途
    /// 没有可插入取消检查的位置——取消只覆盖「逐命中迭代」等于没覆盖扫描本身
    /// （审查缺陷 #1）。按固定窗口分段扫描后，每个窗口开始前都能检查取消令牌。
    /// </summary>
    private const int ScanWindow = 32 * 1024;

    /// <summary>
    /// 全书扫描。cancellationToken 在扫描窗口与逐命中迭代两处检查：Esc、换书或查询变化时
    /// 取消仍在后台执行的扫描，调用方以 OperationCanceledException 识别被取代的请求。
    /// 匹配语义（忽略大小写、字面量）、命中数量与 UTF-16 偏移与整篇扫描一致。
    /// </summary>
    public static (List<int> Offsets, int Length) FindAll(string text, string query, CancellationToken cancellationToken = default)
    {
        var offsets = new List<int>();
        if (string.IsNullOrEmpty(query)) return (offsets, 0);

        var pattern = new StringBuilder();
        foreach (var ch in query)
        {
            pattern.Append(Regex.Escape(ch.ToString()));
        }
        var regex = new System.Text.RegularExpressions.Regex(
            pattern.ToString(),
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        // 窗口之间保留 query.Length-1 个字符的重叠：跨越窗口边界的命中不会被漏掉。
        int overlap = Math.Max(1, query.Length - 1);
        int windowStart = 0;
        while (windowStart < text.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int windowLength = Math.Min(ScanWindow, text.Length - windowStart);
            // 只在 [windowStart, windowStart+windowLength) 内找命中；NextMatch 沿用同一区域。
            for (var match = regex.Match(text, windowStart, windowLength); match.Success; match = match.NextMatch())
            {
                cancellationToken.ThrowIfCancellationRequested();
                // 窗口有重叠：只记录尚未报告过的偏移（结果按偏移升序推进）。
                if (offsets.Count == 0 || match.Index > offsets[^1]) offsets.Add(match.Index);
            }
            if (windowStart + windowLength >= text.Length) break;
            windowStart = Math.Max(windowStart + 1, windowStart + windowLength - overlap);
        }
        return (offsets, query.Length);
    }
}
