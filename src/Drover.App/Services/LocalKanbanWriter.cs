namespace Drover.App.Services;

/// <summary>
/// Rewrites a single line in a markdown file to flip a checkbox marker between
/// <c>[ ]</c>, <c>[/]</c>, and <c>[x]</c>. Preserves BOM and the file's predominant
/// line ending. Stamps <see cref="RecentWriteGuard"/> immediately before flushing so
/// downstream watchers can swallow the echo.
/// </summary>
public static class LocalKanbanWriter
{
    private static readonly System.Text.RegularExpressions.Regex CheckboxRx =
        new(@"^(\s*)([-*])(\s+)\[([ xX/~])\](\s+)(.*)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex HeadingCheckboxRx =
        new(@"^(#{1,6})(\s+)\[([ xX/~])\](\s+)(.*)$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Flips the checkbox marker on <paramref name="lineNumber"/> (1-based) in
    /// <paramref name="filePath"/> to <paramref name="marker"/> (one of <c>' '</c>,
    /// <c>'/'</c>, <c>'~'</c>, <c>'x'</c>). Handles both list-item rows
    /// (<c>- [ ] task</c>) and heading-as-task rows (<c>### [ ] task</c>). Returns
    /// false on I/O error or when the target line doesn't match either pattern.
    /// </summary>
    public static System.Threading.Tasks.Task<bool> SetCheckboxAsync(string filePath, int lineNumber, char marker)
    {
        if (marker != ' ' && marker != '/' && marker != '~' && marker != 'x' && marker != 'X')
            return System.Threading.Tasks.Task.FromResult(false);

        return System.Threading.Tasks.Task.Run(() => Splice(filePath, lineNumber, line =>
        {
            var m = CheckboxRx.Match(line);
            if (m.Success)
                return $"{m.Groups[1].Value}{m.Groups[2].Value}{m.Groups[3].Value}[{marker}]{m.Groups[5].Value}{m.Groups[6].Value}";

            var h = HeadingCheckboxRx.Match(line);
            if (h.Success)
                return $"{h.Groups[1].Value}{h.Groups[2].Value}[{marker}]{h.Groups[4].Value}{h.Groups[5].Value}";

            return null;
        }));
    }

    public static System.Threading.Tasks.Task<bool> ToggleSubItemAsync(string filePath, int lineNumber, bool isDone)
    {
        var marker = isDone ? 'x' : ' ';
        return SetCheckboxAsync(filePath, lineNumber, marker);
    }

    /// <summary>
    /// Atomic single-line update: rewrites the marker (when <paramref name="marker"/>
    /// is non-null) and/or the visible title text (when <paramref name="newTitle"/> is
    /// non-null) on the target line. Preserves the leading indent/prefix and re-attaches
    /// any trailing <c>&lt;!-- gates: ... --&gt;</c> annotation that was on the original
    /// line. Returns false on I/O error or pattern mismatch.
    /// </summary>
    public static System.Threading.Tasks.Task<bool> UpdateTaskLineAsync(string filePath, int lineNumber, char? marker, string? newTitle)
    {
        if (marker is null && newTitle is null) return System.Threading.Tasks.Task.FromResult(true);
        if (marker is char m && m != ' ' && m != '/' && m != '~' && m != 'x' && m != 'X')
            return System.Threading.Tasks.Task.FromResult(false);

        return System.Threading.Tasks.Task.Run(() => Splice(filePath, lineNumber, line =>
        {
            var listMatch = CheckboxRx.Match(line);
            if (listMatch.Success)
            {
                var existingText = listMatch.Groups[6].Value;
                var existingMarker = listMatch.Groups[4].Value[0];
                var nextMarker = marker ?? existingMarker;
                var nextText = newTitle is null ? existingText : ReattachGates(newTitle, existingText);
                return $"{listMatch.Groups[1].Value}{listMatch.Groups[2].Value}{listMatch.Groups[3].Value}[{nextMarker}]{listMatch.Groups[5].Value}{nextText}";
            }

            var headingMatch = HeadingCheckboxRx.Match(line);
            if (headingMatch.Success)
            {
                var existingText = headingMatch.Groups[5].Value;
                var existingMarker = headingMatch.Groups[3].Value[0];
                var nextMarker = marker ?? existingMarker;
                var nextText = newTitle is null ? existingText : ReattachGates(newTitle, existingText);
                return $"{headingMatch.Groups[1].Value}{headingMatch.Groups[2].Value}[{nextMarker}]{headingMatch.Groups[4].Value}{nextText}";
            }

            return null;
        }));
    }

    /// <summary>
    /// Replaces the body lines beneath a heading-style task at <paramref name="headingLineNumber"/>
    /// with <paramref name="newBody"/>. Body ends at the next heading, horizontal rule
    /// (<c>---</c>), or EOF; code fences inside the body are skipped (a <c>#</c> inside
    /// a fenced block does not terminate). The heading line itself is untouched.
    /// </summary>
    public static System.Threading.Tasks.Task<bool> ReplaceHeadingBodyAsync(string filePath, int headingLineNumber, string newBody)
    {
        return System.Threading.Tasks.Task.Run(() => SpliceBlock(filePath, headingLineNumber, newBody ?? string.Empty));
    }

    public static bool IsHeadingTaskLine(string? line)
        => !string.IsNullOrEmpty(line) && HeadingCheckboxRx.IsMatch(line!);

    private static readonly System.Text.RegularExpressions.Regex GatesTrailRx =
        new(@"\s*<!--\s*gates\s*:\s*[^>]+?\s*-->\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// Carries any trailing <c>&lt;!-- gates: ... --&gt;</c> annotation from the original
    /// title onto a user-edited title. The dialog displays the gates-stripped form, so
    /// this re-attaches the marker so saves don't silently drop gate declarations.
    /// </summary>
    private static string ReattachGates(string newTitle, string originalText)
    {
        var match = GatesTrailRx.Match(originalText ?? string.Empty);
        if (!match.Success) return newTitle;
        var trimmed = (newTitle ?? string.Empty).TrimEnd();
        var marker = match.Value.Trim();
        if (trimmed.EndsWith(marker, System.StringComparison.OrdinalIgnoreCase)) return trimmed;
        return string.IsNullOrEmpty(trimmed) ? marker : $"{trimmed} {marker}";
    }

    /// <summary>
    /// Reads <paramref name="filePath"/> as bytes, preserves BOM + dominant line
    /// ending, applies <paramref name="rewriter"/> to the target line, and atomically
    /// flushes. Returns false on any failure (missing file, no match, I/O error).
    /// </summary>
    private static bool Splice(string filePath, int lineNumber, System.Func<string, string?> rewriter)
    {
        if (string.IsNullOrEmpty(filePath) || lineNumber < 1) return false;

        byte[] bytes;
        try { bytes = System.IO.File.ReadAllBytes(filePath); }
        catch { return false; }

        // Detect BOM.
        int bomLen = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bomLen = 3;

        var raw = System.Text.Encoding.UTF8.GetString(bytes, bomLen, bytes.Length - bomLen);
        // Detect dominant line ending. Default to platform if file has none.
        var crlf = raw.Contains("\r\n");
        var hasLoneLf = !crlf && raw.Contains('\n');
        var ending = crlf ? "\r\n" : (hasLoneLf ? "\n" : System.Environment.NewLine);

        // Split preserving exact line content (no trailing \r).
        var normalised = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalised.Split('\n');

        if (lineNumber > lines.Length) return false;
        var idx = lineNumber - 1;
        var rewritten = rewriter(lines[idx]);
        if (rewritten is null) return false;
        if (rewritten == lines[idx]) return true; // no-op, but signal success

        lines[idx] = rewritten;

        // Reassemble with the original ending. Note: trailing newline behaviour —
        // if the original ended with a newline, the last split element is empty and
        // join naturally restores it. If it didn't, we don't add one.
        var joined = string.Join(ending, lines);
        var outBytes = bomLen == 3
            ? Concat(new byte[] { 0xEF, 0xBB, 0xBF }, System.Text.Encoding.UTF8.GetBytes(joined))
            : System.Text.Encoding.UTF8.GetBytes(joined);

        try
        {
            // Stamp BEFORE the write so a watcher event fired during the move is
            // already covered. The guard window is 750ms by default.
            RecentWriteGuard.Stamp(filePath);
            var tmp = filePath + ".tmp";
            System.IO.File.WriteAllBytes(tmp, outBytes);
            System.IO.File.Move(tmp, filePath, overwrite: true);
            // Re-stamp after the move — File.Move can take a few ms and the guard
            // is from-time-of-stamp, so a fresh stamp covers any post-move watcher event.
            RecentWriteGuard.Stamp(filePath);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Multi-line variant of <see cref="Splice"/>: replaces the lines after
    /// <paramref name="headingLineNumber"/> up to the next heading / HR / EOF with
    /// <paramref name="newBody"/>. Code-fenced blocks inside the existing body are
    /// treated as part of the body — a <c>#</c> inside a fence doesn't terminate.
    /// </summary>
    private static bool SpliceBlock(string filePath, int headingLineNumber, string newBody)
    {
        if (string.IsNullOrEmpty(filePath) || headingLineNumber < 1) return false;

        byte[] bytes;
        try { bytes = System.IO.File.ReadAllBytes(filePath); }
        catch { return false; }

        int bomLen = 0;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) bomLen = 3;

        var raw = System.Text.Encoding.UTF8.GetString(bytes, bomLen, bytes.Length - bomLen);
        var crlf = raw.Contains("\r\n");
        var hasLoneLf = !crlf && raw.Contains('\n');
        var ending = crlf ? "\r\n" : (hasLoneLf ? "\n" : System.Environment.NewLine);

        var normalised = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalised.Split('\n');
        if (headingLineNumber > lines.Length) return false;

        // Locate end of body: first line at index >= headingLineNumber that's a
        // heading or HR (outside code fences).
        int bodyStart = headingLineNumber; // 0-based index of first body line
        int bodyEnd = lines.Length;        // exclusive
        bool inFence = false;
        for (int i = bodyStart; i < lines.Length; i++)
        {
            var l = lines[i];
            var trimmed = l.TrimStart();
            if (!inFence)
            {
                if (trimmed.StartsWith("```", System.StringComparison.Ordinal)) { inFence = true; continue; }
                if (l.StartsWith("#")) { bodyEnd = i; break; }
                if (l.Trim() == "---") { bodyEnd = i; break; }
            }
            else
            {
                if (trimmed.StartsWith("```", System.StringComparison.Ordinal)) { inFence = false; }
            }
        }

        // Build the replacement segment. We don't add an extra blank around it; the
        // user's body text owns its leading/trailing whitespace.
        var replacement = (newBody ?? string.Empty).Replace("\r\n", "\n").Replace("\r", "\n");
        var replacementLines = replacement.Length == 0
            ? System.Array.Empty<string>()
            : replacement.Split('\n');

        var rebuilt = new System.Collections.Generic.List<string>(lines.Length - (bodyEnd - bodyStart) + replacementLines.Length);
        for (int i = 0; i < bodyStart; i++) rebuilt.Add(lines[i]);
        rebuilt.AddRange(replacementLines);
        for (int i = bodyEnd; i < lines.Length; i++) rebuilt.Add(lines[i]);

        var joined = string.Join(ending, rebuilt);
        if (joined == raw) return true;

        var outBytes = bomLen == 3
            ? Concat(new byte[] { 0xEF, 0xBB, 0xBF }, System.Text.Encoding.UTF8.GetBytes(joined))
            : System.Text.Encoding.UTF8.GetBytes(joined);

        try
        {
            RecentWriteGuard.Stamp(filePath);
            var tmp = filePath + ".tmp";
            System.IO.File.WriteAllBytes(tmp, outBytes);
            System.IO.File.Move(tmp, filePath, overwrite: true);
            RecentWriteGuard.Stamp(filePath);
            return true;
        }
        catch { return false; }
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var c = new byte[a.Length + b.Length];
        System.Buffer.BlockCopy(a, 0, c, 0, a.Length);
        System.Buffer.BlockCopy(b, 0, c, a.Length, b.Length);
        return c;
    }
}
