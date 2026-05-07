using System.IO;
using System.Text.RegularExpressions;

namespace Drover.App.Models;

/// <summary>
/// Status values used in spec frontmatter. Drover reads these to decide loop
/// flow. The agent is responsible for writing them — see prompt-execute-spec.md.
/// </summary>
public enum SpecStatus
{
    Unknown,
    Pending,
    InProgress,
    Done,
    Blocked,
}

/// <summary>
/// Block severity from the spec frontmatter, set by the agent only when
/// <see cref="SpecStatus.Blocked"/>. <see cref="None"/> means the field is
/// missing or empty (treated as "stop the loop" by the Drover-driven runner —
/// safer default).
/// </summary>
public enum BlockSeverity
{
    None,
    Trivial,
    Major,
}

/// <summary>
/// One parsed spec file from a project's <c>specs/</c> folder. Drover only
/// reads specs; the agent owns writes via the execute-spec prompt. The model
/// is therefore read-only, with a <see cref="Reload"/> helper for the watcher
/// to call after a debounced change event.
/// </summary>
public sealed class SpecFile
{
    /// <summary>Absolute path to the spec file on disk.</summary>
    public string Path { get; }

    /// <summary>Filename (no directory) — what the prompts substitute into
    /// <c>{{SPEC_FILENAME}}</c>.</summary>
    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>Spec id from frontmatter (e.g. "0001"). Empty when missing.</summary>
    public string Id { get; private set; } = string.Empty;

    /// <summary>Spec title from frontmatter. Empty when missing.</summary>
    public string Title { get; private set; } = string.Empty;

    public SpecStatus Status { get; private set; } = SpecStatus.Unknown;

    /// <summary>Raw status text as it appears in the file. Useful for unknown
    /// values; <see cref="Status"/> stays <see cref="SpecStatus.Unknown"/> in
    /// that case.</summary>
    public string RawStatus { get; private set; } = string.Empty;

    public string Created { get; private set; } = string.Empty;
    public string Estimate { get; private set; } = string.Empty;
    public string LastRunAt { get; private set; } = string.Empty;
    public BlockSeverity BlockSeverity { get; private set; } = BlockSeverity.None;
    public string RawBlockSeverity { get; private set; } = string.Empty;

    public IReadOnlyList<string> DependsOn { get; private set; } = System.Array.Empty<string>();

    /// <summary>True when the file was readable and had a frontmatter block.
    /// False on parse failure / missing frontmatter; the runner treats these
    /// as errors.</summary>
    public bool HasFrontmatter { get; private set; }

    private SpecFile(string path) { Path = path; }

    public static SpecFile Load(string path)
    {
        var s = new SpecFile(path);
        s.Reload();
        return s;
    }

    public void Reload()
    {
        Id = Title = Created = Estimate = LastRunAt = RawStatus = RawBlockSeverity = string.Empty;
        Status = SpecStatus.Unknown;
        BlockSeverity = BlockSeverity.None;
        DependsOn = System.Array.Empty<string>();
        HasFrontmatter = false;

        string[] lines;
        try { lines = File.ReadAllLines(Path); }
        catch { return; }

        if (lines.Length == 0 || lines[0].TrimEnd() != "---") return;

        var fm = new List<string>();
        var i = 1;
        for (; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---") { HasFrontmatter = true; break; }
            fm.Add(lines[i]);
        }
        if (!HasFrontmatter) return;

        foreach (var raw in fm)
        {
            var (key, value) = SplitKeyValue(raw);
            if (string.IsNullOrEmpty(key)) continue;
            switch (key)
            {
                case "id":             Id = value; break;
                case "title":          Title = value; break;
                case "status":         RawStatus = value; Status = ParseStatus(value); break;
                case "created":        Created = value; break;
                case "estimate":       Estimate = value; break;
                case "last_run_at":    LastRunAt = value; break;
                case "block_severity": RawBlockSeverity = value; BlockSeverity = ParseSeverity(value); break;
                case "depends_on":     DependsOn = ParseDependsOn(value); break;
            }
        }
    }

    private static (string key, string value) SplitKeyValue(string raw)
    {
        // Strip trailing comment (`# foo`) and whitespace, then split on the
        // first ':'. Quoted values have surrounding quotes stripped; bare
        // values are taken as-is.
        var line = raw;
        // Don't strip `#` inside a quoted string. Cheap check: only strip if
        // there's an unquoted `#`.
        var hashIdx = FindUnquotedHash(line);
        if (hashIdx >= 0) line = line.Substring(0, hashIdx);
        line = line.TrimEnd();
        if (line.Length == 0 || line.TrimStart().StartsWith('#')) return ("", "");
        var colon = line.IndexOf(':');
        if (colon <= 0) return ("", "");
        var key = line.Substring(0, colon).Trim();
        var value = line.Substring(colon + 1).Trim();
        if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            value = value.Substring(1, value.Length - 2);
        else if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'')
            value = value.Substring(1, value.Length - 2);
        return (key, value);
    }

    private static int FindUnquotedHash(string s)
    {
        var inDouble = false; var inSingle = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '#' && !inDouble && !inSingle) return i;
        }
        return -1;
    }

    private static SpecStatus ParseStatus(string v) => v.Trim().ToLowerInvariant() switch
    {
        "pending"     => SpecStatus.Pending,
        "in-progress" => SpecStatus.InProgress,
        "in_progress" => SpecStatus.InProgress,
        "done"        => SpecStatus.Done,
        "blocked"     => SpecStatus.Blocked,
        _             => SpecStatus.Unknown,
    };

    private static BlockSeverity ParseSeverity(string v) => v.Trim().ToLowerInvariant() switch
    {
        "trivial" => BlockSeverity.Trivial,
        "major"   => BlockSeverity.Major,
        _         => BlockSeverity.None,
    };

    private static readonly Regex ListSplit = new(@"\s*,\s*", RegexOptions.Compiled);

    private static IReadOnlyList<string> ParseDependsOn(string v)
    {
        v = v.Trim();
        if (v.Length == 0 || v == "[]") return System.Array.Empty<string>();
        if (v.StartsWith('[') && v.EndsWith(']'))
            v = v.Substring(1, v.Length - 2);
        var parts = ListSplit.Split(v).Where(p => p.Length > 0).Select(p => p.Trim('"', '\'').Trim()).Where(p => p.Length > 0).ToArray();
        return parts;
    }

    /// <summary>
    /// Discovers spec files in <paramref name="specsDir"/> (non-recursive,
    /// <c>*.md</c>), parses each, and returns them sorted by id ascending.
    /// Returns an empty list when the folder is missing.
    /// </summary>
    public static IReadOnlyList<SpecFile> Discover(string specsDir)
    {
        if (string.IsNullOrEmpty(specsDir) || !Directory.Exists(specsDir))
            return System.Array.Empty<SpecFile>();
        IEnumerable<string> files;
        try { files = Directory.EnumerateFiles(specsDir, "*.md", SearchOption.TopDirectoryOnly); }
        catch { return System.Array.Empty<SpecFile>(); }

        var list = files.Select(Load).ToList();
        list.Sort((a, b) => string.Compare(a.Id, b.Id, System.StringComparison.OrdinalIgnoreCase));
        return list;
    }
}
