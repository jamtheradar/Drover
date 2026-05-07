using System.Text.Json;
using Drover.SkillsIndexer;

// Indexer CLI. No flags — reads its sources list from %APPDATA%\Drover\settings.json
// (key `SkillsSources`, falling back to a built-in default if absent or null),
// mirrors each repo into %APPDATA%\Drover\skills-mirror\<owner>-<repo>\, runs the
// resolved parser, and writes the merged index to %APPDATA%\Drover\skills-index.json.
//
// Exit code: 0 on success, 1 if any source errored (the index is still written,
// with `error` set on the failing entries).

var droverData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Drover");
Directory.CreateDirectory(droverData);
var mirrorRoot = Path.Combine(droverData, "skills-mirror");
var indexPath = Path.Combine(droverData, "skills-index.json");
var settingsPath = Path.Combine(droverData, "settings.json");

var sources = ReadSources(settingsPath);
Console.WriteLine($"[indexer] {sources.Count} source(s)");

var indexed = new List<IndexedSource>();
var hadError = false;

foreach (var source in sources)
{
    Console.WriteLine($"[indexer] {source}");
    try
    {
        var mirror = await GitMirror.EnsureAsync(source, mirrorRoot, CancellationToken.None);
        var parser = ParserRegistry.Resolve(source);
        var skills = parser.Parse(mirror.LocalPath);
        Console.WriteLine($"[indexer]   parser={parser.Name} skills={skills.Count}");
        indexed.Add(new IndexedSource(
            Source: source,
            Branch: mirror.Branch,
            CommitSha: mirror.CommitSha,
            MirrorPath: mirror.LocalPath,
            FetchedAt: DateTimeOffset.UtcNow,
            ParserName: parser.Name,
            Skills: skills,
            Error: null));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[indexer]   error: {ex.Message}");
        hadError = true;
        indexed.Add(new IndexedSource(
            Source: source,
            Branch: "",
            CommitSha: null,
            MirrorPath: "",
            FetchedAt: DateTimeOffset.UtcNow,
            ParserName: "",
            Skills: Array.Empty<IndexedSkill>(),
            Error: ex.Message));
    }
}

var index = new SkillsIndex(
    Version: 1,
    GeneratedAt: DateTimeOffset.UtcNow,
    Sources: indexed);

var json = JsonSerializer.Serialize(index, new JsonSerializerOptions
{
    WriteIndented = true,
    DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
});
File.WriteAllText(indexPath, json);
Console.WriteLine($"[indexer] wrote {indexPath}");
return hadError ? 1 : 0;

static List<string> ReadSources(string settingsPath)
{
    var defaults = new List<string>
    {
        "BehiSecc/awesome-claude-skills",
        "alirezarezvani/claude-skills",
        "davila7/claude-code-templates",
    };
    if (!File.Exists(settingsPath)) return defaults;
    try
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
        if (!doc.RootElement.TryGetProperty("SkillsSources", out var arr) ||
            arr.ValueKind != JsonValueKind.Array)
            return defaults;
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { } s && !string.IsNullOrWhiteSpace(s))
                list.Add(s.Trim());
        return list.Count == 0 ? defaults : list;
    }
    catch { return defaults; }
}
