using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Drover.App.Services;

/// <summary>
/// Tiny inline-markdown parser for plan summary / task text. Recognises
/// `**bold**`, `*italic*`, `` `code` `` and `[text](url)` only — no headings,
/// no code fences, no nested formatting. Anything unmatched is emitted as
/// plain text. Designed for one-line styling, not full markdown rendering.
/// </summary>
public static class MarkdownInline
{
    private static readonly Regex Rx = new(
        @"(\*\*(?<b>[^*]+)\*\*)|(\*(?<i>[^*]+)\*)|(`(?<c>[^`]+)`)|(\[(?<lt>[^\]]+)\]\((?<lu>[^)]+)\))",
        RegexOptions.Compiled);

    private static readonly SolidColorBrush CodeBg = new(Color.FromArgb(0x40, 0x4a, 0x90, 0xe2));
    private static readonly SolidColorBrush CodeFg = new(Color.FromRgb(0xb5, 0xe8, 0xff));
    private static readonly SolidColorBrush LinkFg = new(Color.FromRgb(0x6c, 0xb6, 0xff));
    static MarkdownInline()
    {
        CodeBg.Freeze();
        CodeFg.Freeze();
        LinkFg.Freeze();
    }

    public static IEnumerable<Inline> Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        int last = 0;
        foreach (Match m in Rx.Matches(text))
        {
            if (m.Index > last)
                yield return new Run(text.Substring(last, m.Index - last));

            if (m.Groups["b"].Success)
                yield return new Bold(new Run(m.Groups["b"].Value));
            else if (m.Groups["i"].Success)
                yield return new Italic(new Run(m.Groups["i"].Value));
            else if (m.Groups["c"].Success)
                yield return MakeCode(m.Groups["c"].Value);
            else if (m.Groups["lt"].Success)
                yield return MakeLink(m.Groups["lt"].Value, m.Groups["lu"].Value);

            last = m.Index + m.Length;
        }
        if (last < text.Length)
            yield return new Run(text.Substring(last));
    }

    private static Run MakeCode(string s)
        => new(s)
        {
            FontFamily = new FontFamily("Cascadia Code, Consolas"),
            Background = CodeBg,
            Foreground = CodeFg,
        };

    private static Hyperlink MakeLink(string text, string url)
    {
        var h = new Hyperlink(new Run(text)) { Foreground = LinkFg };
        try { h.NavigateUri = new System.Uri(url, System.UriKind.RelativeOrAbsolute); } catch { }
        h.RequestNavigate += (_, e) =>
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch { }
            e.Handled = true;
        };
        return h;
    }
}

/// <summary>
/// Attached property that turns a markdown-flavoured string into the
/// `TextBlock.Inlines` collection. Use on summary or task-text TextBlocks
/// where you want light inline styling without bringing in a full markdown
/// renderer.
/// </summary>
public static class InlineMd
{
    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(InlineMd),
        new PropertyMetadata(null, OnSourceChanged));

    public static string? GetSource(DependencyObject d) => (string?)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, string? v) => d.SetValue(SourceProperty, v);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var s = e.NewValue as string;
        if (string.IsNullOrEmpty(s)) return;
        foreach (var inl in MarkdownInline.Parse(s)) tb.Inlines.Add(inl);
    }
}

/// <summary>
/// Attached property for plan section headings: splits a leading numeric
/// prefix (e.g. "1.", "2.1", "3.2.1") and renders it with the TextBlock's
/// inherited heading brush, while the remaining body is rendered Bold and
/// in the theme's foreground colour. Falls back to plain text when no
/// numeric prefix is present.
/// </summary>
public static class HeadingMd
{
    private static readonly Regex NumberPrefixRx = new(
        @"^(?<num>\d+(?:\.\d+)*\.?)\s+(?<rest>.*)$",
        RegexOptions.Compiled);

    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(string), typeof(HeadingMd),
        new PropertyMetadata(null, OnSourceChanged));

    public static string? GetSource(DependencyObject d) => (string?)d.GetValue(SourceProperty);
    public static void SetSource(DependencyObject d, string? v) => d.SetValue(SourceProperty, v);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        var s = e.NewValue as string;
        if (string.IsNullOrEmpty(s)) return;

        var m = NumberPrefixRx.Match(s);
        if (!m.Success)
        {
            tb.Inlines.Add(new Run(s));
            return;
        }

        var num = new Run(m.Groups["num"].Value + " ");
        num.SetResourceReference(TextElement.ForegroundProperty, "MdTheme.OrderedListMark");
        tb.Inlines.Add(num);
        var body = new Bold(new Run(m.Groups["rest"].Value));
        body.SetResourceReference(TextElement.ForegroundProperty, "MdTheme.Foreground");
        tb.Inlines.Add(body);
    }
}
