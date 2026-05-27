using System.IO;

namespace Drover.App.Services;

/// <summary>
/// Wires the Codex CLI's hook system into Drover's loopback gateway. Reuses
/// the existing <c>drover-hook.ps1</c> forwarder script that
/// <see cref="HooksInstaller"/> already writes — Codex posts the same
/// JSON-on-stdin envelope shape Claude does, so the script doesn't need to
/// know which agent invoked it. The gateway tags by <c>DROVER_SESSION_ID</c>
/// from the tab's environment.
///
/// Two state-mutating responsibilities, both idempotent:
/// <list type="number">
/// <item>Write Drover's six event entries into <c>hooks.json</c> at user or
/// project scope, preserving any user-authored entries.</item>
/// <item>Ensure <c>[features] hooks = true</c> exists in the user's
/// <c>~/.codex/config.toml</c> (and strip the now-deprecated
/// <c>codex_hooks</c> key if it was left behind by an earlier Drover). The
/// file is round-tripped — pre-existing sections and keys are left in place.</item>
/// </list>
///
/// Default scope is user-level (<c>~/.codex/</c>). Codex's project-local hook
/// path silently no-ops until the user runs <c>codex trust .</c>, so the
/// menu-driven project install surface exists but is opt-in; the watchdog in
/// <see cref="ViewModels.TerminalTabViewModel"/> nudges the user toward
/// <c>codex trust</c> if events don't flow.
/// </summary>
public sealed class CodexHooksInstaller
{
    private readonly HooksInstaller _hooksInstaller;

    /// <summary>Absolute path to the forwarder script — shared with
    /// <see cref="HooksInstaller"/>, written there on demand.</summary>
    public string ScriptPath { get; }

    /// <summary>User-scope <c>~/.codex/hooks.json</c>.</summary>
    public string UserHooksPath { get; }

    /// <summary>User-scope <c>~/.codex/config.toml</c>.</summary>
    public string UserConfigPath { get; }

    public CodexHooksInstaller(HooksInstaller hooksInstaller)
    {
        _hooksInstaller = hooksInstaller;
        ScriptPath = hooksInstaller.ScriptPath;

        var home = Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dotCodex = Path.Combine(home, ".codex");
        UserHooksPath = Path.Combine(dotCodex, "hooks.json");
        UserConfigPath = Path.Combine(dotCodex, "config.toml");
    }

    /// <summary>
    /// Installs Drover's six event entries into <c>~/.codex/hooks.json</c>
    /// and ensures <c>[features] hooks = true</c> in
    /// <c>~/.codex/config.toml</c>. Re-running rewrites Drover's entries while
    /// preserving user-authored ones. Returns true on success; false on any
    /// IO/parse error.
    /// </summary>
    public bool TryInstallUser()
    {
        try
        {
            EnsureScript();
            CodexHookSettingsMerger.UpsertHooksJson(UserHooksPath, ScriptPath, BuildCommand(ScriptPath));
            CodexHookSettingsMerger.EnsureFeatureFlagToml(
                UserConfigPath, "hooks", deprecatedKeys: new[] { "codex_hooks" });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Strips Drover's six entries from <c>~/.codex/hooks.json</c>. The
    /// <c>hooks</c> feature flag is intentionally left alone — it's a global
    /// toggle and removing it could break the user's own hooks.
    /// Returns true on success or when nothing needed doing.
    /// </summary>
    public bool TryUninstallUser()
    {
        try
        {
            CodexHookSettingsMerger.RemoveDroverEntriesFromHooksJson(UserHooksPath, ScriptPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Installs Drover's six event entries into the project's
    /// <c>&lt;project&gt;/.codex/hooks.json</c>. Project-scope only takes effect
    /// once the user runs <c>codex trust .</c> in the project directory, hence
    /// the watchdog hint in the terminal tab. Returns true on success.
    /// </summary>
    public bool TryInstallProject(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return false;
        try
        {
            EnsureScript();
            var target = Path.Combine(projectPath, ".codex", "hooks.json");
            CodexHookSettingsMerger.UpsertHooksJson(target, ScriptPath, BuildCommand(ScriptPath));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when a Drover entry (matched by script-path substring)
    /// exists in <c>&lt;project&gt;/.codex/hooks.json</c>.</summary>
    public bool IsInstalledForProject(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath)) return false;
        var path = Path.Combine(projectPath, ".codex", "hooks.json");
        return ContainsDroverHook(path);
    }

    /// <summary>True when a Drover entry exists in
    /// <c>~/.codex/hooks.json</c>.</summary>
    public bool IsInstalledForUser() => ContainsDroverHook(UserHooksPath);

    private bool ContainsDroverHook(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            var json = File.ReadAllText(path);
            var escaped = ScriptPath.Replace("\\", "\\\\");
            return json.Contains(ScriptPath, StringComparison.OrdinalIgnoreCase)
                || json.Contains(escaped, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Ensures the shared forwarder script is on disk. Delegates to
    /// <see cref="HooksInstaller.EnsureScript"/> — Codex-only users may never
    /// click Claude's per-project install, so we can't assume the file exists.
    /// </summary>
    private void EnsureScript() => _hooksInstaller.EnsureScript();

    private static string BuildCommand(string scriptPath)
    {
        // Codex's `type: "command"` schema accepts a single command string.
        // -NoProfile keeps fire-and-forget latency low; quote the path so
        // spaces in %APPDATA% don't break it.
        return $"pwsh -NoProfile -File \"{scriptPath}\"";
    }
}
