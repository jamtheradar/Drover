using Drover.App.Models;

namespace Drover.App.Services;

/// <summary>
/// Discriminator stamped on every <see cref="HookEvent"/> so downstream consumers
/// can branch on which agent produced the envelope. Resolved at the gateway from
/// the registering tab's <see cref="ProjectKind"/>: Claude / ClaudeDirect →
/// <see cref="Claude"/>, Codex → <see cref="Codex"/>. Pwsh tabs don't register a
/// hook handler at all, so no third value is needed; if a new agent ships later
/// the enum grows then.
/// </summary>
public enum HookAgentKind
{
    Claude,
    Codex
}

public static class HookAgentKinds
{
    /// <summary>
    /// Maps a <see cref="ProjectKind"/> to the matching <see cref="HookAgentKind"/>,
    /// or <c>null</c> when the kind doesn't run hooks (e.g. <see cref="ProjectKind.Pwsh"/>).
    /// Callers must skip <see cref="HooksGateway.Register"/> when this returns null.
    /// </summary>
    public static HookAgentKind? FromProjectKind(ProjectKind kind) => kind switch
    {
        ProjectKind.Claude or ProjectKind.ClaudeDirect => HookAgentKind.Claude,
        ProjectKind.Codex => HookAgentKind.Codex,
        _ => null,
    };
}
