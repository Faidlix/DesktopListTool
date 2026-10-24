// 功能：將便籤中的常用 Markdown 安全轉成桌面繪製片段，不執行 HTML 或外部程式碼。
// 使用：DesktopLayeredNoteForm 逐行呼叫 ParseLine；支援標題、引言、項目符號、勾選、連結、粗體、斜體、刪除線與行內程式碼。
// 設定：編輯器永遠保存原始 Markdown，這個類別只負責顯示文字與樣式。

using System.Text.RegularExpressions;

namespace DesktopListTool;

internal sealed record MarkdownRun(string Text, FontStyle Style, bool IsCode = false);
internal sealed record MarkdownLine(IReadOnlyList<MarkdownRun> Runs, int HeadingLevel = 0, string Prefix = "", bool? Checked = null);

internal static partial class MarkdownNoteRenderer
{
    public static MarkdownLine ParseLine(string source)
    {
        var text = source.TrimEnd();
        var heading = 0;
        while (heading < Math.Min(6, text.Length) && text[heading] == '#') heading++;
        if (heading > 0 && heading < text.Length && text[heading] == ' ') text = text[(heading + 1)..]; else heading = 0;
        string prefix = "";
        bool? isChecked = null;
        var task = TaskRegex().Match(text);
        if (task.Success)
        {
            isChecked = task.Groups[1].Value.Equals("x", StringComparison.OrdinalIgnoreCase);
            text = task.Groups[2].Value;
        }
        else if (text.StartsWith("> ")) { prefix = "❯ "; text = text[2..]; }
        else if (text.StartsWith("- ") || text.StartsWith("* ") || text.StartsWith("+ ")) { prefix = "• "; text = text[2..]; }
        else
        {
            var ordered = OrderedRegex().Match(text);
            if (ordered.Success) { prefix = ordered.Groups[1].Value + ". "; text = ordered.Groups[2].Value; }
        }
        return new MarkdownLine(ParseInline(text), heading, prefix, isChecked);
    }

    private static IReadOnlyList<MarkdownRun> ParseInline(string text)
    {
        var result = new List<MarkdownRun>();
        for (var i = 0; i < text.Length;)
        {
            if (TryDelimited(text, ref i, "**", FontStyle.Bold, result) || TryDelimited(text, ref i, "__", FontStyle.Bold, result) ||
                TryDelimited(text, ref i, "~~", FontStyle.Strikeout, result) || TryDelimited(text, ref i, "*", FontStyle.Italic, result) ||
                TryDelimited(text, ref i, "_", FontStyle.Italic, result) || TryCode(text, ref i, result) || TryLink(text, ref i, result)) continue;
            var next = FindNextMarker(text, i + 1);
            result.Add(new MarkdownRun(text[i..next], FontStyle.Regular));
            i = next;
        }
        return result.Count == 0 ? [new MarkdownRun("", FontStyle.Regular)] : result;
    }

    private static bool TryDelimited(string text, ref int index, string marker, FontStyle style, List<MarkdownRun> result)
    {
        if (!text.AsSpan(index).StartsWith(marker)) return false;
        var end = text.IndexOf(marker, index + marker.Length, StringComparison.Ordinal);
        if (end < 0) return false;
        result.Add(new MarkdownRun(text.Substring(index + marker.Length, end - index - marker.Length), style));
        index = end + marker.Length; return true;
    }
    private static bool TryCode(string text, ref int index, List<MarkdownRun> result)
    {
        if (text[index] != '`') return false; var end = text.IndexOf('`', index + 1); if (end < 0) return false;
        result.Add(new MarkdownRun(text.Substring(index + 1, end - index - 1), FontStyle.Regular, true)); index = end + 1; return true;
    }
    private static bool TryLink(string text, ref int index, List<MarkdownRun> result)
    {
        if (text[index] != '[') return false; var close = text.IndexOf("](", index, StringComparison.Ordinal); if (close < 0) return false;
        var end = text.IndexOf(')', close + 2); if (end < 0) return false;
        result.Add(new MarkdownRun(text.Substring(index + 1, close - index - 1), FontStyle.Underline)); index = end + 1; return true;
    }
    private static int FindNextMarker(string text, int start)
    {
        var positions = new[] { text.IndexOf("**", start, StringComparison.Ordinal), text.IndexOf("__", start, StringComparison.Ordinal), text.IndexOf("~~", start, StringComparison.Ordinal), text.IndexOf('*', start), text.IndexOf('_', start), text.IndexOf('`', start), text.IndexOf('[', start) };
        return positions.Where(value => value >= 0).DefaultIfEmpty(text.Length).Min();
    }

    [GeneratedRegex(@"^[-*+]\s+\[([ xX])\]\s+(.*)$")]
    private static partial Regex TaskRegex();
    [GeneratedRegex(@"^(\d+)\.\s+(.*)$")]
    private static partial Regex OrderedRegex();
}
