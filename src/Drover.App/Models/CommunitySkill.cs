using System.Text.Json.Serialization;

namespace Drover.App.Models;

// One skill discovered in a community GitHub source. `Source` is `owner/repo`
// (matching the SkillsSummary convention so the GitHub-link converter still
// works); `Branch` is captured at fetch time so install URLs stay correct
// even if the repo moves to `main` later. `FolderPath` is the repo-relative
// path of the directory that contains SKILL.md — `Files` lists every blob
// underneath it so install can clone the whole folder.
public sealed record CommunitySkill(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("folderPath")] string FolderPath,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files)
{
    // Stable identity used for dedup across sources. Folder name is the
    // useful handle — two different repos shipping `code-review/SKILL.md`
    // are almost certainly the same skill, regardless of where they live.
    public string DedupKey => Slug.ToLowerInvariant();
}

public sealed record CommunitySource(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("treeSha")] string TreeSha,
    [property: JsonPropertyName("fetchedAt")] DateTimeOffset FetchedAt,
    [property: JsonPropertyName("skills")] IReadOnlyList<CommunitySkill> Skills,
    [property: JsonPropertyName("error")] string? Error);

public sealed record CommunityCatalogCache(
    [property: JsonPropertyName("sources")] Dictionary<string, CommunitySource> Sources);
