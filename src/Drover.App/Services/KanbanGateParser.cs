namespace Drover.App.Services;

/// <summary>
/// Extracts gate declarations from a card's source line. v1 syntax: a trailing
/// <c>&lt;!-- gates: a,b,c --&gt;</c> HTML comment. Whitespace is tolerated; gate
/// names are split on commas, trimmed, and lowercased.
/// </summary>
public static class KanbanGateParser
{
    private static readonly System.Text.RegularExpressions.Regex GatesRx =
        new(@"<!--\s*gates\s*:\s*([^>]+?)\s*-->",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static System.Collections.Generic.IReadOnlyList<string> Parse(string? source)
    {
        if (string.IsNullOrEmpty(source)) return System.Array.Empty<string>();
        var m = GatesRx.Match(source!);
        if (!m.Success) return System.Array.Empty<string>();
        var raw = m.Groups[1].Value;
        var parts = raw.Split(',');
        var result = new System.Collections.Generic.List<string>(parts.Length);
        foreach (var p in parts)
        {
            var trimmed = p.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(trimmed)) result.Add(trimmed);
        }
        return result;
    }

    /// <summary>Strips the trailing <c>&lt;!-- gates: ... --&gt;</c> comment, returning a clean title.</summary>
    public static string StripGatesMarker(string? source)
    {
        if (string.IsNullOrEmpty(source)) return string.Empty;
        var stripped = GatesRx.Replace(source!, string.Empty).TrimEnd();
        return stripped;
    }
}
