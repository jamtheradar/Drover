using System.IO;

namespace Drover.App.Services;

public enum LoopMode
{
    Drover,
    Session,
}

/// <summary>
/// Reads and writes a tiny project-level config at
/// <c>{projectRoot}/.drover/config.md</c>. Single setting in v1:
/// <c>default_loop_mode</c>. Format is markdown-with-frontmatter so users can
/// edit it by hand and add their own notes after the frontmatter block —
/// matches the rest of <c>.drover/</c>.
/// </summary>
public interface ILoopConfigService
{
    LoopMode GetDefaultMode(string projectRoot);
    void SetDefaultMode(string projectRoot, LoopMode mode);
}

public sealed class LoopConfigService : ILoopConfigService
{
    private const string DroverFolder = ".drover";
    private const string ConfigFileName = "config.md";
    private const string ModeKey = "default_loop_mode";

    public LoopMode GetDefaultMode(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot)) return LoopMode.Drover;
        var path = Path.Combine(projectRoot, DroverFolder, ConfigFileName);
        if (!File.Exists(path)) return LoopMode.Drover;

        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch { return LoopMode.Drover; }

        if (lines.Length == 0 || lines[0].TrimEnd() != "---") return LoopMode.Drover;
        for (var i = 1; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd() == "---") break;
            var line = StripComment(lines[i]).TrimEnd();
            var colon = line.IndexOf(':');
            if (colon <= 0) continue;
            var key = line.Substring(0, colon).Trim();
            if (!string.Equals(key, ModeKey, System.StringComparison.OrdinalIgnoreCase)) continue;
            var value = line.Substring(colon + 1).Trim().Trim('"', '\'').Trim();
            return Parse(value);
        }
        return LoopMode.Drover;
    }

    public void SetDefaultMode(string projectRoot, LoopMode mode)
    {
        if (string.IsNullOrEmpty(projectRoot))
            throw new System.ArgumentException("projectRoot is required", nameof(projectRoot));

        var dir = Path.Combine(projectRoot, DroverFolder);
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, ConfigFileName);

        var serialised = Serialise(mode);

        if (!File.Exists(path))
        {
            AtomicFile.WriteAllText(path, BuildDefaultDocument(serialised));
            return;
        }

        // Preserve the body (notes after frontmatter) and any unknown
        // frontmatter keys. Only rewrite the default_loop_mode line; if it's
        // missing, append it inside the frontmatter block.
        string original;
        try { original = File.ReadAllText(path); }
        catch
        {
            AtomicFile.WriteAllText(path, BuildDefaultDocument(serialised));
            return;
        }

        var rewritten = Rewrite(original, serialised);
        AtomicFile.WriteAllText(path, rewritten);
    }

    private static string Rewrite(string original, string serialised)
    {
        var newline = original.Contains("\r\n") ? "\r\n" : "\n";
        var lines = original.Split(new[] { "\r\n", "\n" }, System.StringSplitOptions.None).ToList();

        if (lines.Count == 0 || lines[0].TrimEnd() != "---")
        {
            // No frontmatter — prepend a fresh block, keep existing body.
            var prefix = $"---{newline}{ModeKey}: {serialised}{newline}---{newline}{newline}";
            return prefix + original;
        }

        var fmEnd = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].TrimEnd() == "---") { fmEnd = i; break; }
        }
        if (fmEnd < 0)
        {
            // Unterminated frontmatter — overwrite with a sane default rather
            // than guess where the body starts.
            return BuildDefaultDocument(serialised);
        }

        var found = false;
        for (var i = 1; i < fmEnd; i++)
        {
            var stripped = StripComment(lines[i]);
            var colon = stripped.IndexOf(':');
            if (colon <= 0) continue;
            var key = stripped.Substring(0, colon).Trim();
            if (!string.Equals(key, ModeKey, System.StringComparison.OrdinalIgnoreCase)) continue;
            // Preserve any trailing comment from the original line.
            var commentIdx = lines[i].IndexOf('#');
            var trailingComment = commentIdx > colon ? "  " + lines[i].Substring(commentIdx) : string.Empty;
            lines[i] = $"{ModeKey}: {serialised}{trailingComment}";
            found = true;
            break;
        }

        if (!found)
        {
            lines.Insert(fmEnd, $"{ModeKey}: {serialised}");
        }

        return string.Join(newline, lines);
    }

    private static string BuildDefaultDocument(string serialised)
    {
        var nl = System.Environment.NewLine;
        return $"---{nl}{ModeKey}: {serialised}  # drover | session{nl}---{nl}{nl}# Drover project config{nl}{nl}Notes for humans, not parsed by Drover.{nl}";
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

    private static LoopMode Parse(string v) => v.Trim().ToLowerInvariant() switch
    {
        // Accept both the canonical "session" and the longer "session-driven"
        // form (spec 0012's test plan demonstrates users may hand-edit either).
        "session" or "session-driven" => LoopMode.Session,
        _                             => LoopMode.Drover,
    };

    private static string Serialise(LoopMode mode) => mode switch
    {
        LoopMode.Session => "session",
        _                => "drover",
    };
}
