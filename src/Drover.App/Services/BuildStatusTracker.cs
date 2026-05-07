using System.Collections.Concurrent;
using Drover.App.Models;

namespace Drover.App.Services;

public sealed record BuildOutcome(System.DateTime AtUtc, int ExitCode, string? Detail);

/// <summary>
/// Tracks the most recent <c>dotnet build</c> outcome per project, derived from
/// <see cref="HooksGateway"/> Bash <c>PostToolUse</c> events. Project key = the
/// command's <c>cwd</c> (when present in the hook envelope), falling back to the
/// active tab's project path at the time of the event.
///
/// v1: only watches commands beginning with <c>dotnet build</c>. Future: extend to
/// <c>dotnet test</c>, <c>npm test</c>, etc.
/// </summary>
public sealed class BuildStatusTracker
{
    private readonly ConcurrentDictionary<string, BuildOutcome> _byProject = new(System.StringComparer.OrdinalIgnoreCase);

    public BuildOutcome? GetLastFor(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath)) return null;
        return _byProject.TryGetValue(projectPath, out var v) ? v : null;
    }

    /// <summary>
    /// Inspects a hook event; if it's a <c>PostToolUse</c> Bash event whose command
    /// starts with <c>dotnet build</c>, records the exit code keyed by the envelope's
    /// <c>cwd</c>. Falls back to <paramref name="fallbackProjectPath"/> when cwd is
    /// not present.
    /// </summary>
    public void Ingest(HookEvent evt, string? fallbackProjectPath)
    {
        if (evt is null) return;
        if (!string.Equals(evt.Type, "PostToolUse", System.StringComparison.OrdinalIgnoreCase)) return;
        if (!string.Equals(evt.Tool, "Bash", System.StringComparison.OrdinalIgnoreCase)) return;
        if (string.IsNullOrWhiteSpace(evt.RawBody)) return;

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(evt.RawBody);
            var root = doc.RootElement;
            if (!root.TryGetProperty("tool_input", out var toolInput)) return;
            if (!toolInput.TryGetProperty("command", out var cmdEl) || cmdEl.ValueKind != System.Text.Json.JsonValueKind.String) return;
            var cmd = cmdEl.GetString() ?? "";
            if (!cmd.TrimStart().StartsWith("dotnet build", System.StringComparison.OrdinalIgnoreCase)) return;

            int exitCode = 0;
            string? detail = null;
            if (root.TryGetProperty("tool_response", out var resp) &&
                resp.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (resp.TryGetProperty("returncode", out var rc) && rc.TryGetInt32(out var rcv)) exitCode = rcv;
                else if (resp.TryGetProperty("exit_code", out var ec) && ec.TryGetInt32(out var ecv)) exitCode = ecv;
                if (resp.TryGetProperty("stderr", out var se) && se.ValueKind == System.Text.Json.JsonValueKind.String)
                    detail = Truncate(se.GetString() ?? string.Empty);
            }

            string? cwd = null;
            if (root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == System.Text.Json.JsonValueKind.String)
                cwd = cwdEl.GetString();
            var key = !string.IsNullOrEmpty(cwd) ? cwd! : fallbackProjectPath;
            if (string.IsNullOrEmpty(key)) return;

            _byProject[key!] = new BuildOutcome(System.DateTime.UtcNow, exitCode, detail);
        }
        catch { /* malformed payload — ignore */ }
    }

    private static string Truncate(string s) => s.Length <= 240 ? s : s.Substring(0, 240) + "…";
}
