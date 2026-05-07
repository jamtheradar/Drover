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

public sealed record KanbanBadges(
    string? GitStatus = null,
    System.DateTime? LastModifiedUtc = null,
    bool ActivePulse = false);

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
        SubChecklist.Count > 0 || Gates.Count > 0 || SourceRef is LocalFileRef;

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
            var parts = new System.Collections.Generic.List<string>(3);
            if (SubChecklist.Count > 0)
            {
                int done = 0;
                for (int i = 0; i < SubChecklist.Count; i++) if (SubChecklist[i].IsDone) done++;
                parts.Add($"{done}/{SubChecklist.Count} subtasks");
            }
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
