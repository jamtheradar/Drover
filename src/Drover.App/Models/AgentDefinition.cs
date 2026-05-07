namespace Drover.App.Models;

public enum AgentScope
{
    User,
    Project,
}

/// <summary>
/// A Claude Code subagent discovered in <c>~/.claude/agents/</c> (User scope) or
/// <c>&lt;project&gt;/.claude/agents/</c> (Project scope). Frontmatter is parsed line-based;
/// multi-line block scalars take the first line only.
/// </summary>
public sealed record AgentDefinition(
    string Id,
    string Name,
    string? Description,
    System.Collections.Generic.IReadOnlyList<string> Tools,
    string? Model,
    string FilePath,
    AgentScope Scope);
