namespace Drover.App.Models;

/// <summary>
/// One entry from a Claude Code todo/task tool call. Status string is preserved
/// raw (one of <c>pending|in_progress|completed</c>); callers map to
/// <see cref="KanbanStatus"/>. Id is the task id reported by the tool (string,
/// since newer Task* tools use ids like <c>"1"</c>, <c>"2"</c> and may eventually
/// prefix them).
/// </summary>
public sealed record TodoEntry(string Id, string Content, string Status, string? ActiveForm);
