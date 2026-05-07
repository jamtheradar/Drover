using System.IO;
using System.Text;
using Drover.App.Models;

namespace Drover.App.Services;

/// <summary>
/// Surgical writer for <c>specs/{NNNN}-{slug}.md</c>. Updates the title,
/// status, block_severity frontmatter keys and the body, preserving every
/// other key, comment, ordering, and the original line-ending convention.
/// Mirrors the approach in <see cref="LoopConfigService"/>.
/// </summary>
public static class SpecFileWriter
{
    public static bool TryUpdate(
        string path,
        string title,
        SpecStatus status,
        BlockSeverity severity,
        string body,
        out string error)
    {
        error = string.Empty;
        string original;
        try { original = File.ReadAllText(path); }
        catch (System.Exception ex) { error = ex.Message; return false; }

        var newline = original.Contains("\r\n") ? "\r\n" : "\n";
        var lines = original.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None).ToList();

        if (lines.Count == 0 || lines[0].TrimEnd() != "---")
        {
            error = "Spec is missing frontmatter (no leading `---`).";
            return false;
        }

        var fmEnd = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].TrimEnd() == "---") { fmEnd = i; break; }
        }
        if (fmEnd < 0)
        {
            error = "Spec frontmatter is unterminated (no closing `---`).";
            return false;
        }

        SetOrInsert(lines, 1, fmEnd, "title", QuoteIfNeeded(title), ref fmEnd);
        SetOrInsert(lines, 1, fmEnd, "status", StatusToString(status), ref fmEnd);

        var severityValue = status == SpecStatus.Blocked ? SeverityToString(severity) : string.Empty;
        SetOrInsert(lines, 1, fmEnd, "block_severity", severityValue, ref fmEnd);

        // Replace body — everything after the closing `---`. Drop a single
        // blank line that conventionally follows it; we'll re-emit one.
        var newLines = lines.Take(fmEnd + 1).ToList();
        var bodyText = (body ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        if (bodyText.Length == 0)
        {
            newLines.Add(string.Empty);
        }
        else
        {
            newLines.Add(string.Empty);
            foreach (var bl in bodyText.Split('\n')) newLines.Add(bl);
        }

        var rewritten = string.Join(newline, newLines);
        if (!rewritten.EndsWith(newline)) rewritten += newline;

        try { AtomicFile.WriteAllText(path, rewritten); }
        catch (System.Exception ex) { error = ex.Message; return false; }
        return true;
    }

    private static void SetOrInsert(System.Collections.Generic.List<string> lines, int fmStart, int fmEnd, string key, string value, ref int fmEndRef)
    {
        for (var i = fmStart; i < fmEnd; i++)
        {
            var stripped = StripComment(lines[i]);
            var colon = stripped.IndexOf(':');
            if (colon <= 0) continue;
            var k = stripped.Substring(0, colon).Trim();
            if (!string.Equals(k, key, System.StringComparison.OrdinalIgnoreCase)) continue;

            var commentIdx = lines[i].IndexOf('#');
            var trailingComment = commentIdx > colon ? "  " + lines[i].Substring(commentIdx) : string.Empty;
            lines[i] = $"{key}: {value}{trailingComment}";
            return;
        }
        lines.Insert(fmEnd, $"{key}: {value}");
        fmEndRef++;
    }

    private static string StripComment(string line)
    {
        var inDouble = false; var inSingle = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '#' && !inDouble && !inSingle) return line.Substring(0, i);
        }
        return line;
    }

    private static string QuoteIfNeeded(string s)
    {
        s ??= string.Empty;
        if (s.Length == 0) return "\"\"";
        var needsQuote = false;
        foreach (var c in s)
        {
            if (c == ':' || c == '#' || c == '"' || c == '\'' || c == '\\' || c == '\n' || c == '\r' || c == '[' || c == ']' || c == '{' || c == '}')
            {
                needsQuote = true; break;
            }
        }
        if (!needsQuote && (s[0] == ' ' || s[^1] == ' ')) needsQuote = true;
        if (!needsQuote) return s;

        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var c in s)
        {
            if (c == '\\' || c == '"') { sb.Append('\\'); sb.Append(c); }
            else if (c == '\n') sb.Append("\\n");
            else if (c == '\r') sb.Append("\\r");
            else sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string StatusToString(SpecStatus s) => s switch
    {
        SpecStatus.Pending    => "pending",
        SpecStatus.InProgress => "in-progress",
        SpecStatus.Done       => "done",
        SpecStatus.Blocked    => "blocked",
        _                     => "pending",
    };

    private static string SeverityToString(BlockSeverity s) => s switch
    {
        BlockSeverity.Trivial => "trivial",
        BlockSeverity.Major   => "major",
        _                     => string.Empty,
    };
}
