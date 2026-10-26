namespace Drover.App.Models;

public enum KanbanStatus
{
    Pending,
    Planned,
    InProgress,
    Completed,
}

/// <summary>
/// Source of a kanban card. Distinguishes local-file-backed cards (writable),
/// live TodoWrite cards (read-only, ephemeral), and agent catalog entries
/// (no status, render as a rail).
/// </summary>
public abstract record KanbanSourceRef;

public sealed record LocalFileRef(string AbsolutePath, int LineNumber, int IndentLevel) : KanbanSourceRef;
public sealed record LiveTodoRef(string SessionId, string Id) : KanbanSourceRef;
public sealed record AgentRef(string AgentId) : KanbanSourceRef;

public sealed record KanbanGate(string Kind, bool Passed, string? Detail);

public sealed record KanbanSubItem(int LineNumber, string Text, bool IsDone, int IndentLevel);

/// <summary>
/// Card decorations. Every member is optional and defaulted, so the Local and
/// Live sources construct this exactly as they did before the Agents source
/// grew spec awareness.
/// </summary>
/// <param name="AssignedSpecs">Specs whose <c>agent:</c> names this agent. 0 for
/// non-agent cards, which keeps the specs meta segment omitted entirely.</param>
/// <param name="DoneSpecs">Subset of <paramref name="AssignedSpecs"/> at
/// <c>status: done</c>.</param>
/// <param name="RunningSpecLabel">Pre-rendered label for the first assigned spec
/// sitting at <c>status: in-progress</c> (e.g. <c>&#x25B6; 0015</c>). Derived from
/// frontmatter, deliberately independent of <paramref name="ActivePulse"/>: a
/// session that died mid-run leaves the label up with no pulse, which is the most
/// useful thing this card can tell you.</param>
/// <param name="AssignedSpecIds">Ids of the assigned specs, for the expanded body.
/// Ids only - titles live in the Specs panel.</param>
public sealed record KanbanBadges(
    string? GitStatus = null,
    System.DateTime? LastModifiedUtc = null,
    bool ActivePulse = false,
    int AssignedSpecs = 0,
    int DoneSpecs = 0,
    string? RunningSpecLabel = null,
    System.Collections.Generic.IReadOnlyList<string>? AssignedSpecIds = null);

public sealed record KanbanCard(
    string Id,
    KanbanStatus Status,
    string Title,
    string? Body,
    System.Collections.Generic.IReadOnlyList<KanbanGate> Gates,
    System.Collections.Generic.IReadOnlyList<KanbanSubItem> SubChecklist,
    KanbanSourceRef SourceRef,
    KanbanBadges Badges)
{
    public static KanbanCard Empty(string id, KanbanStatus status, string title, KanbanSourceRef src) =>
        new(id, status, title, null,
            System.Array.Empty<KanbanGate>(), System.Array.Empty<KanbanSubItem>(), src, new KanbanBadges());

    // View-only helpers consumed by KanbanCardControl. Records are immutable and the VM
    // replaces card instances on every state change, so bindings re-read these on swap —
    // no INPC required.

    /// <summary>True when there's something worth showing in the expanded card body
    /// (subtasks, gates, or a local-file source label).</summary>
    public bool HasDetails =>
        SubChecklist.Count > 0 || Gates.Count > 0 || SourceRef is LocalFileRef
        || Badges.AssignedSpecIds is { Count: > 0 };

    /// <summary>True when the collapsed meta line has content. Drives the line's
    /// visibility so a bare card doesn't reserve vertical space for an empty row.</summary>
    public bool HasMeta => !string.IsNullOrEmpty(MetaSummary);

    /// <summary>One-line summary shown under the title in collapsed state. Combines
    /// subtask count, source basename, and relative-modified time, separated by · . Each
    /// segment is omitted when empty so the line stays short.</summary>
    public string MetaSummary
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>(5);
            if (SubChecklist.Count > 0)
            {
                int done = 0;
                for (int i = 0; i < SubChecklist.Count; i++) if (SubChecklist[i].IsDone) done++;
                parts.Add($"{done}/{SubChecklist.Count} subtasks");
            }
            // Both segments stay absent at their defaults, so Local/Live meta
            // lines are byte-identical to before.
            if (Badges.AssignedSpecs > 0)
                parts.Add($"{Badges.DoneSpecs}/{Badges.AssignedSpecs} specs");
            if (!string.IsNullOrEmpty(Badges.RunningSpecLabel))
                parts.Add(Badges.RunningSpecLabel!);
            if (SourceRef is LocalFileRef lf)
                parts.Add(System.IO.Path.GetFileName(lf.AbsolutePath));
            if (Badges.LastModifiedUtc is { } utc)
                parts.Add(FormatRelative(utc));
            return string.Join(" · ", parts);
        }
    }

    private static string FormatRelative(System.DateTime utc)
    {
        var local = utc.ToLocalTime();
        var delta = System.DateTime.Now - local;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d ago";
        return local.ToString("MMM d");
    }
}
