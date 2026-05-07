using System.Text.Json.Serialization;

namespace Drover.SkillsIndexer;

// Drover-owned schema for the on-disk skills index. Stable contract: Drover.App
// reads this verbatim, so additions go on the end and removals are breaking.
//
// `MirrorPath` is an absolute path to the local clone — install copies blobs
// from there, never from raw.githubusercontent. `Files` are repo-relative,
// rooted at MirrorPath.

public sealed record IndexedSkill(
    [property: JsonPropertyName("slug")] string Slug,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("folderPath")] string FolderPath,
    [property: JsonPropertyName("files")] IReadOnlyList<string> Files,
    [property: JsonPropertyName("plugin")] string? Plugin = null);

public sealed record IndexedSource(
    [property: JsonPropertyName("source")] string Source,
    [property: JsonPropertyName("branch")] string Branch,
    [property: JsonPropertyName("commitSha")] string? CommitSha,
    [property: JsonPropertyName("mirrorPath")] string MirrorPath,
    [property: JsonPropertyName("fetchedAt")] DateTimeOffset FetchedAt,
    [property: JsonPropertyName("parser")] string ParserName,
    [property: JsonPropertyName("skills")] IReadOnlyList<IndexedSkill> Skills,
    [property: JsonPropertyName("error")] string? Error);

public sealed record SkillsIndex(
    [property: JsonPropertyName("version")] int Version,
    [property: JsonPropertyName("generatedAt")] DateTimeOffset GeneratedAt,
    [property: JsonPropertyName("sources")] IReadOnlyList<IndexedSource> Sources);
