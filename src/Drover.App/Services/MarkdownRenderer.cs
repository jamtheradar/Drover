using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace Drover.App.Services;

/// <summary>
/// Minimal Markdown → FlowDocument renderer. Handles headings, paragraphs, fenced code blocks,
/// inline code, bold, italic, blockquotes, bullets/numbered lists, and links — enough to render
/// CLAUDE.md and memory shard files cleanly. Not a full CommonMark parser.
/// </summary>
public static class MarkdownRenderer
{
    private static readonly Brush Text = new SolidColorBrush(Color.FromRgb(0xdd, 0xdd, 0xdd));
    private static readonly Brush Muted = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x4e, 0xc9, 0xb0));
    private static readonly Brush Code = new SolidColorBrush(Color.FromRgb(0xce, 0x91, 0x78));
    private static readonly Brush CodeBg = new SolidColorBrush(Color.FromRgb(0x1b, 0x1b, 0x1c));
    private static readonly Brush Quote = new SolidColorBrush(Color.FromRgb(0x2a, 0x2a, 0x2b));
    private static readonly Brush Link = new SolidColorBrush(Color.FromRgb(0x4f, 0xa8, 0xff));

    public static FlowDocument Render(string markdown)
    {
        var doc = new FlowDocument
        {
            Background = Brushes.Transparent,
            Foreground = Text,
            FontFamily = new FontFamily("Segoe UI, Helvetica, Arial"),
            FontSize = 13,
            PagePadding = new Thickness(0),
            LineHeight = 20,
        };
        if (string.IsNullOrEmpty(markdown)) return doc;

        var lines = markdown.Replace("\r\n", "\n").Split('\n');
        int i = 0;
        while (i < lines.Length)
        {
            var line = lines[i];

            // Fenced code block
            if (line.TrimStart().StartsWith("```"))
            {
                var sb = new System.Text.StringBuilder();
                i++;
                while (i < lines.Length && !lines[i].TrimStart().StartsWith("```"))
                {
                    sb.AppendLine(lines[i]);
                    i++;
                }
                if (i < lines.Length) i++;
                doc.Blocks.Add(MakeCodeBlock(sb.ToString().TrimEnd('\r', '\n')));
                continue;
            }

            // Heading
            if (line.StartsWith("### "))
            {
                doc.Blocks.Add(MakeHeading(line[4..], 16, FontWeights.SemiBold, Text));
                i++; continue;
            }
            if (line.StartsWith("## "))
            {
                doc.Blocks.Add(MakeHeading(line[3..], 19, FontWeights.SemiBold, Text));
                i++; continue;
            }
            if (line.StartsWith("# "))
            {
                doc.Blocks.Add(MakeHeading(line[2..], 24, FontWeights.Bold, Text));
                i++; continue;
            }

            // Horizontal rule
            if (line.Trim() == "---" || line.Trim() == "***")
            {
                doc.Blocks.Add(new BlockUIContainer(new System.Windows.Shapes.Rectangle
                {
                    Height = 1, Fill = Muted, Margin = new Thickness(0, 8, 0, 8)
                }));
                i++; continue;
            }

            // Blockquote (one or more consecutive '>' lines)
            if (line.StartsWith(">"))
            {
                var quote = new List<string>();
                while (i < lines.Length && lines[i].StartsWith(">"))
                {
                    quote.Add(lines[i].TrimStart('>').TrimStart());
                    i++;
                }
                var sec = new Section
                {
                    Background = Quote,
                    BorderBrush = Accent,
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding = new Thickness(12, 6, 8, 6),
                    Margin = new Thickness(0, 4, 0, 8),
                };
                var p = new Paragraph { Margin = new Thickness(0) };
                AppendInline(p, string.Join(" ", quote));
                sec.Blocks.Add(p);
                doc.Blocks.Add(sec);
                continue;
            }

            // Lists
            if (Regex.IsMatch(line, @"^[\s]*[-*]\s+") || Regex.IsMatch(line, @"^[\s]*\d+\.\s+"))
            {
                var ordered = Regex.IsMatch(line, @"^[\s]*\d+\.\s+");
                var list = new List
                {
                    MarkerStyle = ordered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                    Margin = new Thickness(0, 4, 0, 8),
                    Padding = new Thickness(20, 0, 0, 0),
                };
                while (i < lines.Length && (Regex.IsMatch(lines[i], @"^[\s]*[-*]\s+") || Regex.IsMatch(lines[i], @"^[\s]*\d+\.\s+")))
                {
                    var content = Regex.Replace(lines[i], @"^[\s]*([-*]|\d+\.)\s+", "");
                    var li = new ListItem();
                    var p = new Paragraph { Margin = new Thickness(0, 0, 0, 2) };
                    AppendInline(p, content);
                    li.Blocks.Add(p);
                    list.ListItems.Add(li);
                    i++;
                }
                doc.Blocks.Add(list);
                continue;
            }

            // Blank line
            if (string.IsNullOrWhiteSpace(line)) { i++; continue; }

            // Paragraph: gather until blank line or block-starter
            var para = new System.Text.StringBuilder(line);
            i++;
            while (i < lines.Length && !string.IsNullOrWhiteSpace(lines[i])
                   && !lines[i].StartsWith("#") && !lines[i].TrimStart().StartsWith("```")
                   && !lines[i].StartsWith(">")
                   && !Regex.IsMatch(lines[i], @"^[\s]*[-*]\s+")
                   && !Regex.IsMatch(lines[i], @"^[\s]*\d+\.\s+"))
            {
                para.Append(' ').Append(lines[i]);
                i++;
            }
            var pp = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            AppendInline(pp, para.ToString());
            doc.Blocks.Add(pp);
        }

        return doc;
    }

    private static Paragraph MakeHeading(string text, double size, FontWeight weight, Brush foreground)
    {
        var p = new Paragraph
        {
            FontSize = size,
            FontWeight = weight,
            Foreground = foreground,
            Margin = new Thickness(0, 12, 0, 6),
        };
        AppendInline(p, text);
        return p;
    }

    private static Section MakeCodeBlock(string code)
    {
        var sec = new Section
        {
            Background = CodeBg,
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 4, 0, 8),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
        };
        var p = new Paragraph
        {
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            FontSize = 12,
            Foreground = Code,
            Margin = new Thickness(0),
        };
        p.Inlines.Add(new Run(code));
        sec.Blocks.Add(p);
        return sec;
    }

    // Inline formatting: `code`, **bold**, *italic*, [text](url)
    private static readonly Regex InlineRegex = new(
        @"(?<code>`[^`\n]+`)|(?<bold>\*\*[^*\n]+\*\*)|(?<italic>\*[^*\n]+\*|_[^_\n]+_)|(?<link>\[[^\]\n]+\]\([^)\n]+\))",
        RegexOptions.Compiled);

    private static void AppendInline(Paragraph para, string text)
    {
        int idx = 0;
        foreach (Match m in InlineRegex.Matches(text))
        {
            if (m.Index > idx)
                para.Inlines.Add(new Run(text[idx..m.Index]));
            if (m.Groups["code"].Success)
            {
                var run = new Run(m.Value.Trim('`')) { FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"), Foreground = Code, Background = CodeBg };
                para.Inlines.Add(run);
            }
            else if (m.Groups["bold"].Success)
            {
                var b = new Bold(); b.Inlines.Add(new Run(m.Value.Trim('*'))); para.Inlines.Add(b);
            }
            else if (m.Groups["italic"].Success)
            {
                var v = m.Value;
                var inner = v.Trim('*').Trim('_');
                var it = new Italic(); it.Inlines.Add(new Run(inner)); para.Inlines.Add(it);
            }
            else if (m.Groups["link"].Success)
            {
                var v = m.Value;
                var close = v.IndexOf(']');
                var label = v[1..close];
                var url = v[(close + 2)..^1];
                var hl = new Hyperlink(new Run(label)) { Foreground = Link, NavigateUri = TryUri(url) };
                hl.RequestNavigate += (s, e) =>
                {
                    try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = e.Uri.ToString(), UseShellExecute = true }); } catch { }
                    e.Handled = true;
                };
                para.Inlines.Add(hl);
            }
            idx = m.Index + m.Length;
        }
        if (idx < text.Length)
            para.Inlines.Add(new Run(text[idx..]));
    }

    private static System.Uri? TryUri(string url)
    {
        return System.Uri.TryCreate(url, System.UriKind.Absolute, out var u) ? u : null;
    }
}
