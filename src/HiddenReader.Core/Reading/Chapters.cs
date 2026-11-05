using System.Text.RegularExpressions;

namespace HiddenReader.Core.Reading;

public sealed record ChapterInfo(int Offset, string Title);

/// <summary>识别 TXT 中的章节标题并返回（偏移, 标题）列表，按出现顺序排列。</summary>
public static partial class Chapters
{
    [GeneratedRegex(
        @"^\s*(?:第\s*[0-9〇零一二三四五六七八九十百千万两]+\s*[章节卷回部篇集]|序章|楔子|引子|前言|后记|尾声|终章|番外|[Cc]hapter\s+\d+).{0,40}$",
        RegexOptions.Multiline)]
    private static partial Regex ChapterRegex();

    public static List<ChapterInfo> Detect(string text)
    {
        var result = new List<ChapterInfo>();
        if (string.IsNullOrEmpty(text)) return result;

        foreach (Match m in ChapterRegex().Matches(text))
        {
            // 只认可独行标题：上一字符应是换行或文本开头（Multiline 的 ^ 已保证），且标题行内无长句。
            string title = m.Value.Trim();
            if (title.Length is > 0 and <= 50)
            {
                result.Add(new ChapterInfo(m.Index, title));
            }
        }
        return result;
    }
}
