namespace Drover.App.Services;

public sealed record MarkdownTheme(
    string Background, string Foreground, string LineNumbers, string Selection,
    string H1, string H2, string H3, string H456,
    string Bold, string Italic, string BoldItalic, string Strike,
    string InlineCodeFg, string InlineCodeBg, string CodeBlockFg, string CodeBlockBg,
    string Link, string Url, string Image,
    string ListMark, string Quote, string HR, string Table,
    string TaskOpen, string TaskDone, string Html, string Escape,
    bool HeadingMarkerOnly = false,
    string? OrderedListMark = null);

public static class MarkdownThemes
{
    public const string DefaultName = "Drover Dark";

    public static IReadOnlyDictionary<string, MarkdownTheme> All { get; } = new Dictionary<string, MarkdownTheme>
    {
        ["Drover Dark"] = new(
            Background: "#1B1B1C", Foreground: "#D4D4D4", LineNumbers: "#555555", Selection: "#264F78",
            H1: "#8AB4F8", H2: "#7FA7E5", H3: "#6F94CF", H456: "#5E81B5",
            Bold: "#E8E8E8", Italic: "#D4D4D4", BoldItalic: "#F0E0A0", Strike: "#777777",
            InlineCodeFg: "#B5E8FF", InlineCodeBg: "#1F2A3A", CodeBlockFg: "#B5E8FF", CodeBlockBg: "#152030",
            Link: "#6CB6FF", Url: "#4A9CD6", Image: "#C586C0",
            ListMark: "#C586C0", Quote: "#888888", HR: "#666666", Table: "#9CDCFE",
            TaskOpen: "#FFB454", TaskDone: "#7EC07E", Html: "#888888", Escape: "#999999"),

        ["Solarized Dark"] = new(
            Background: "#002B36", Foreground: "#93A1A1", LineNumbers: "#586E75", Selection: "#073642",
            H1: "#268BD2", H2: "#2AA198", H3: "#859900", H456: "#B58900",
            Bold: "#EEE8D5", Italic: "#93A1A1", BoldItalic: "#CB4B16", Strike: "#586E75",
            InlineCodeFg: "#2AA198", InlineCodeBg: "#073642", CodeBlockFg: "#2AA198", CodeBlockBg: "#073642",
            Link: "#268BD2", Url: "#6C71C4", Image: "#D33682",
            ListMark: "#D33682", Quote: "#586E75", HR: "#586E75", Table: "#2AA198",
            TaskOpen: "#CB4B16", TaskDone: "#859900", Html: "#586E75", Escape: "#657B83"),

        ["Solarized Light"] = new(
            Background: "#FDF6E3", Foreground: "#657B83", LineNumbers: "#93A1A1", Selection: "#EEE8D5",
            H1: "#268BD2", H2: "#2AA198", H3: "#859900", H456: "#B58900",
            Bold: "#073642", Italic: "#586E75", BoldItalic: "#CB4B16", Strike: "#93A1A1",
            InlineCodeFg: "#2AA198", InlineCodeBg: "#EEE8D5", CodeBlockFg: "#2AA198", CodeBlockBg: "#EEE8D5",
            Link: "#268BD2", Url: "#6C71C4", Image: "#D33682",
            ListMark: "#D33682", Quote: "#93A1A1", HR: "#93A1A1", Table: "#2AA198",
            TaskOpen: "#CB4B16", TaskDone: "#859900", Html: "#93A1A1", Escape: "#586E75"),

        ["GitHub Light"] = new(
            Background: "#FFFFFF", Foreground: "#1F2328", LineNumbers: "#8C959F", Selection: "#B6E3FF",
            H1: "#0969DA", H2: "#1F6FEB", H3: "#388BFD", H456: "#57A6FF",
            Bold: "#1F2328", Italic: "#1F2328", BoldItalic: "#8250DF", Strike: "#8C959F",
            InlineCodeFg: "#0550AE", InlineCodeBg: "#EAEEF2", CodeBlockFg: "#1F2328", CodeBlockBg: "#F6F8FA",
            Link: "#0969DA", Url: "#0550AE", Image: "#8250DF",
            ListMark: "#8250DF", Quote: "#656D76", HR: "#D0D7DE", Table: "#0550AE",
            TaskOpen: "#BF8700", TaskDone: "#1A7F37", Html: "#656D76", Escape: "#656D76"),

        ["Sublime Markdown"] = new(
            Background: "#202830", Foreground: "#DDDDDD", LineNumbers: "#5A6878", Selection: "#3A536B",
            H1: "#F92672", H2: "#FD971F", H3: "#E6DB74", H456: "#A6E22E",
            Bold: "#FD971F", Italic: "#66D9EF", BoldItalic: "#F92672", Strike: "#75715E",
            InlineCodeFg: "#A6E22E", InlineCodeBg: "#1A2028", CodeBlockFg: "#A6E22E", CodeBlockBg: "#1A2028",
            Link: "#66D9EF", Url: "#AE81FF", Image: "#F92672",
            ListMark: "#F92672", Quote: "#9AA5B0", HR: "#75715E", Table: "#66D9EF",
            TaskOpen: "#FD971F", TaskDone: "#A6E22E", Html: "#75715E", Escape: "#AE81FF",
            HeadingMarkerOnly: true,
            OrderedListMark: "#FD971F"),

        ["Monokai"] = new(
            Background: "#272822", Foreground: "#F8F8F2", LineNumbers: "#75715E", Selection: "#49483E",
            H1: "#A6E22E", H2: "#A6E22E", H3: "#A6E22E", H456: "#A6E22E",
            Bold: "#F8F8F2", Italic: "#F8F8F2", BoldItalic: "#FD971F", Strike: "#75715E",
            InlineCodeFg: "#E6DB74", InlineCodeBg: "#3E3D32", CodeBlockFg: "#E6DB74", CodeBlockBg: "#1E1F1C",
            Link: "#66D9EF", Url: "#AE81FF", Image: "#F92672",
            ListMark: "#F92672", Quote: "#75715E", HR: "#75715E", Table: "#66D9EF",
            TaskOpen: "#FD971F", TaskDone: "#A6E22E", Html: "#75715E", Escape: "#AE81FF"),
    };

    public static MarkdownTheme Get(string? name)
        => name is not null && All.TryGetValue(name, out var t) ? t : All[DefaultName];
}
