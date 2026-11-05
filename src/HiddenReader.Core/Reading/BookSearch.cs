using System.Text;
using System.Text.RegularExpressions;

namespace HiddenReader.Core.Reading;

/// <summary>全书搜索：返回所有命中的起始偏移（升序），忽略大小写。</summary>
public static class BookSearch
{
    public static (List<int> Offsets, int Length) FindAll(string text, string query)
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

        foreach (Match m in regex.Matches(text))
        {
            offsets.Add(m.Index);
        }
        return (offsets, query.Length);
    }
}
