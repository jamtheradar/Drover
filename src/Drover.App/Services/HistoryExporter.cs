using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Drover.App.ViewModels;

namespace Drover.App.Services;

/// <summary>
/// Reads a tab's raw VT session log, strips ANSI/VT control sequences down to
/// human-readable text, and writes a markdown copy under
/// %APPDATA%\Drover\exports\. Returns the export path on success or null if the
/// source log isn't available.
///
/// Stripping is deliberately conservative — we drop CSI/OSC/DCS/SOS/PM/APC
/// escapes and bare control bytes that would render as junk, but keep newlines,
/// tabs, and printable Unicode (including Claude's TUI box-drawing). The output
/// still contains redraw artefacts (option lists rendered N times as the user
/// arrowed through them) — this is option #2 from the design discussion: read,
/// not curated transcript.
/// </summary>
public static class HistoryExporter
{
    /// <summary>CSI (ESC [ ... final-byte) and similar two-byte escapes.</summary>
    private static readonly Regex CsiOrShort = new(
        @"\x1b\[[\x30-\x3f]*[\x20-\x2f]*[\x40-\x7e]|\x1b[\x20-\x2f]*[\x30-\x7e]",
        RegexOptions.Compiled);

    /// <summary>OSC / DCS / SOS / PM / APC strings — terminated by BEL or ST (ESC \).</summary>
    private static readonly Regex OscLike = new(
        @"\x1b[\]PX^_][\s\S]*?(?:\x07|\x1b\\)",
        RegexOptions.Compiled);

    /// <summary>Stray control bytes we don't care about (keeps \t \n \r).</summary>
    private static readonly Regex StrayControls = new(
        @"[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]",
        RegexOptions.Compiled);

    public static string? Export(TerminalTabViewModel tab)
    {
        var src = tab.LogFilePath;
        if (string.IsNullOrEmpty(src) || !File.Exists(src)) return null;

        // Push any in-flight chunks to disk so the export reflects what the user
        // sees right now, not what the writer happened to flush 250 ms ago.
        tab.FlushSessionLog();

        string raw;
        try { raw = File.ReadAllText(src, Encoding.UTF8); }
        catch { return null; }

        var stripped = Strip(raw);

        var dir = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData),
            "Drover", "exports");
        Directory.CreateDirectory(dir);

        var stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeTitle = Sanitize(tab.Title);
        var path = Path.Combine(dir, $"{stamp}_{safeTitle}.md");

        var sb = new StringBuilder(stripped.Length + 256);
        sb.Append("# ").Append(tab.Title).Append('\n');
        sb.Append("- **Project:** ").Append(tab.Project.Name).Append('\n');
        sb.Append("- **Path:** ").Append(tab.Project.Path).Append('\n');
        sb.Append("- **Exported:** ").Append(System.DateTime.Now.ToString("O")).Append('\n');
        sb.Append("- **Source log:** `").Append(src).Append("`\n\n");
        sb.Append("```\n");
        sb.Append(stripped);
        if (!stripped.EndsWith('\n')) sb.Append('\n');
        sb.Append("```\n");

        try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); }
        catch { return null; }

        return path;
    }

    private static string Strip(string input)
    {
        var s = OscLike.Replace(input, string.Empty);
        s = CsiOrShort.Replace(s, string.Empty);
        s = StrayControls.Replace(s, string.Empty);
        // Normalize CR-only line endings (common from PTY) to LF for markdown viewers.
        s = s.Replace("\r\n", "\n").Replace('\r', '\n');
        return s;
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        return sb.Length == 0 ? "tab" : sb.ToString();
    }
}
