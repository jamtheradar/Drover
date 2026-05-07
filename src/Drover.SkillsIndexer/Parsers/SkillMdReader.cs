namespace Drover.SkillsIndexer.Parsers;

internal static class SkillMdReader
{
    // Lightweight YAML frontmatter reader. Returns (name, description); both
    // are nullable. We only care about top-level scalar `name:` / `description:`
    // pairs — full YAML parsing is overkill for what SKILL.md files actually
    // contain.
    public static (string? Name, string? Description) Parse(string text)
    {
        string? name = null;
        string? description = null;

        if (text.StartsWith("---"))
        {
            var end = text.IndexOf("\n---", 3, StringComparison.Ordinal);
            if (end > 0)
            {
                var frontmatter = text.Substring(3, end - 3);
                foreach (var raw in frontmatter.Split('\n'))
                {
                    var line = raw.TrimEnd('\r');
                    var colon = line.IndexOf(':');
                    if (colon < 1) continue;
                    var key = line.Substring(0, colon).Trim().ToLowerInvariant();
                    var value = line[(colon + 1)..].Trim().Trim('"', '\'');
                    if (key == "name" && string.IsNullOrEmpty(name)) name = value;
                    else if (key == "description" && string.IsNullOrEmpty(description)) description = value;
                }
                text = text[(end + 4)..];
            }
        }

        if (string.IsNullOrEmpty(description))
        {
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;
                description = line.Length > 400 ? line[..400] + "…" : line;
                break;
            }
        }
        return (name, description);
    }
}
