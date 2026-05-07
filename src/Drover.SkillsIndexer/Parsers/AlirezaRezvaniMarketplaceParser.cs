using System.Text.Json;

namespace Drover.SkillsIndexer.Parsers;

// alirezarezvani/claude-skills publishes a single .claude-plugin/marketplace.json
// listing every plugin in the repo. We treat each plugin as one installable unit
// (matches `claude plugin install` semantics — the plugin folder ships its own
// SKILL.md, commands, hooks, etc. as one bundle).
//
// Plugin schema observed in the repo (subset we need):
//   { "plugins": [ { "name": "...", "source": "./plugins/foo",
//                    "description": "...", "version": "..." } ] }
//
// The `source` field is a repo-relative directory; we use it as both the
// install folder and the slug fallback.
public sealed class AlirezaRezvaniMarketplaceParser : ISkillSourceParser
{
    public string Name => "AlirezaRezvani.Marketplace";

    public bool CanParse(string source)
        => string.Equals(source, "alirezarezvani/claude-skills", StringComparison.OrdinalIgnoreCase);

    public IReadOnlyList<IndexedSkill> Parse(string repoRoot)
    {
        var manifestPath = Path.Combine(repoRoot, ".claude-plugin", "marketplace.json");
        if (!File.Exists(manifestPath)) return Array.Empty<IndexedSkill>();

        using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
        if (!doc.RootElement.TryGetProperty("plugins", out var plugins) ||
            plugins.ValueKind != JsonValueKind.Array)
            return Array.Empty<IndexedSkill>();

        var skills = new List<IndexedSkill>();
        foreach (var p in plugins.EnumerateArray())
        {
            var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
            var src = p.TryGetProperty("source", out var s) ? s.GetString() : null;
            var desc = p.TryGetProperty("description", out var d) ? d.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(src)) continue;

            // `source` is typically `./plugins/<name>` — normalise to a
            // repo-relative folder for our `FolderPath`.
            var folder = src.TrimStart('.', '/', '\\').Replace('\\', '/');
            var folderAbs = Path.Combine(repoRoot, folder.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(folderAbs)) continue;

            var files = new List<string>();
            foreach (var f in Directory.EnumerateFiles(folderAbs, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(repoRoot, f).Replace('\\', '/');
                files.Add(rel);
            }

            skills.Add(new IndexedSkill(
                Slug: name,
                Name: name,
                Description: desc,
                FolderPath: folder,
                Files: files,
                Plugin: name));
        }
        return skills;
    }
}
