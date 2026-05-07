namespace Drover.SkillsIndexer.Parsers;

// Fallback parser. Walks the repo for any folder containing SKILL.md and
// treats it as one skill. Matches the original tree-walking behaviour from
// SkillsRepoCatalog but runs against the local mirror, so no network calls
// per skill. Skips bin/, obj/, node_modules/, .git/.
public sealed class DefaultSkillMdParser : ISkillSourceParser
{
    public string Name => "Default";

    public bool CanParse(string source) => true;

    public IReadOnlyList<IndexedSkill> Parse(string repoRoot)
    {
        var skills = new List<IndexedSkill>();
        var skillMdFiles = new List<string>();
        Walk(repoRoot, skillMdFiles);

        foreach (var skillMd in skillMdFiles)
        {
            var folderAbs = Path.GetDirectoryName(skillMd)!;
            var folderRel = Path.GetRelativePath(repoRoot, folderAbs).Replace('\\', '/');
            var slug = Path.GetFileName(folderAbs);

            string? name = null, description = null;
            try
            {
                var text = File.ReadAllText(skillMd);
                (name, description) = SkillMdReader.Parse(text);
            }
            catch { /* description optional */ }

            var files = new List<string>();
            foreach (var f in Directory.EnumerateFiles(folderAbs, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(repoRoot, f).Replace('\\', '/');
                files.Add(rel);
            }

            skills.Add(new IndexedSkill(
                Slug: slug,
                Name: string.IsNullOrEmpty(name) ? Humanize(slug) : name!,
                Description: description,
                FolderPath: folderRel,
                Files: files));
        }

        return skills;
    }

    private static void Walk(string dir, List<string> result)
    {
        // Manual walk so we can prune noisy directories without paying the
        // cost of EnumerateFiles' full descent.
        foreach (var sub in Directory.EnumerateDirectories(dir))
        {
            var name = Path.GetFileName(sub);
            if (name is ".git" or "node_modules" or "bin" or "obj" or ".vs" or "dist" or "build") continue;
            Walk(sub, result);
        }
        foreach (var f in Directory.EnumerateFiles(dir))
        {
            if (string.Equals(Path.GetFileName(f), "SKILL.md", StringComparison.OrdinalIgnoreCase))
                result.Add(f);
        }
    }

    private static string Humanize(string slug)
    {
        var parts = slug.Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
            if (parts[i].Length > 0)
                parts[i] = char.ToUpperInvariant(parts[i][0]) + parts[i][1..];
        return string.Join(' ', parts);
    }
}
