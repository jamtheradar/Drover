using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Drover.App.Models;

public sealed record ProjectDefinition(
    string Name,
    string Path,
    ProjectKind Kind,
    string? Command = null,
    string? Args = null,
    Dictionary<string, string>? EnvVars = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PlansFolder = null,
    /// <summary>Hex color (e.g. "#3D7EFF") applied as the tab header background. Null = use the default chrome.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TabColor = null,
    /// <summary>Raw command line passed straight to ConPTY, bypassing the pwsh wrapper. When set,
    /// Kind/Command/Args/EnvVars/Resume/DangerouslySkipPermissions are ignored — the user owns the
    /// full launch line (shell choice, cwd, env, claude flags). Use for shell experiments.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? LaunchCommand = null,
    /// <summary>Per-project --model override for Claude tabs. Overrides AppSettings.DefaultClaudeModel.
    /// Ignored when explicit --model is already in Args.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? Model = null);

public enum ProjectKind
{
    Claude,
    Pwsh,
    /// <summary>Launch <c>claude.exe</c> straight from CreateProcess — no pwsh/cmd wrapper. Working
    /// directory and environment (DROVER_SESSION_ID/HOOKS_URL, global+per-project env) are passed
    /// via the native API instead of shell builtins. Faster startup, no shell quirks; loses pwsh
    /// niceties (no shell after Claude exits — the tab shows "Session Terminated").</summary>
    ClaudeDirect,
    /// <summary>Launch the OpenAI Codex CLI inside the standard pwsh wrapper. Mirrors the
    /// <see cref="Claude"/> branch's env-var injection (DROVER_SESSION_ID / DROVER_HOOKS_URL) and
    /// the per-project --model flag, but skips the Claude-only knobs (no --resume, no
    /// --dangerously-skip-permissions, no CLAUDE_CODE_NO_FLICKER). The default command resolves
    /// to <c>codex</c> on PATH unless <see cref="AppSettings.CodexExecutablePath"/> is set.</summary>
    Codex,
    /// <summary>Launch the Google Gemini CLI inside the standard pwsh wrapper. Mirrors the
    /// <see cref="Codex"/> branch's env-var injection (DROVER_SESSION_ID / DROVER_HOOKS_URL) and
    /// the per-project --model flag, but skips the Claude-only knobs (no --resume, no
    /// --dangerously-skip-permissions, no CLAUDE_CODE_NO_FLICKER). The default command resolves
    /// to <c>gemini</c> on PATH unless <see cref="AppSettings.GeminiExecutablePath"/> is set.</summary>
    Gemini
}
