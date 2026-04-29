using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Drover.App.Services;

/// <summary>
/// Bootstraps Claude Code's hooks integration: writes our forwarder script to
/// <c>%APPDATA%\Drover\hooks\drover-hook.ps1</c>, then merges hook entries
/// pointing at it into <c>%USERPROFILE%\.claude\settings.json</c>. The script
/// is a no-op when DROVER_HOOKS_URL / DROVER_SESSION_ID aren't set, so leaving
/// the entries in place when Drover isn't running is harmless.
///
/// Idempotent: re-running rewrites our entries while preserving any other
/// hooks the user has configured. Identification is by exact script-path match
/// in the command string.
/// </summary>
public sealed class HooksInstaller
{
    // Bumped on script-content change so existing installs get the new script.
    private const string ScriptVersion = "1";
    private const string StatusScriptVersion = "1";

    private const string ScriptContent = """
        # Drover hook forwarder. Posts the Claude Code hook event JSON (read from
        # stdin) to the Drover loopback listener tagged with the session id from
        # the environment. Silent and best-effort: any failure is swallowed so a
        # broken Drover process never blocks the Claude Code turn.
        $ErrorActionPreference = 'SilentlyContinue'
        try {
            $url = $env:DROVER_HOOKS_URL
            $sid = $env:DROVER_SESSION_ID
            if (-not $url -or -not $sid) { exit 0 }
            $body = [Console]::In.ReadToEnd()
            Invoke-RestMethod -Uri $url -Method Post -Body $body `
                -ContentType 'application/json' `
                -Headers @{ 'X-Drover-Session' = $sid } `
                -TimeoutSec 2 | Out-Null
        } catch { }
        exit 0
        """;

    // Statusline forwarder. Same shape as the hook forwarder but adds the
    // X-Drover-Kind header so the gateway routes the rich CC StatusJSON to
    // the per-tab status handler instead of the hook-event pipeline. Must
    // write *something* to stdout because Claude Code uses that as the
    // rendered status line. We write an empty line — Drover renders its own
    // chrome around the terminal, so we don't want a duplicate inline.
    private const string StatusScriptContent = """
        # Drover statusLine forwarder. Posts the Claude Code statusLine JSON
        # (read from stdin) to the Drover loopback listener with kind=statusline.
        # Best-effort: failures are swallowed so a stale Drover never blocks the
        # CC refresh loop. Always writes an empty line to stdout so CC has a
        # status string to render (Drover renders the rich version itself).
        $ErrorActionPreference = 'SilentlyContinue'
        try {
            $body = [Console]::In.ReadToEnd()
            $url = $env:DROVER_HOOKS_URL
            $sid = $env:DROVER_SESSION_ID
            if ($url -and $sid -and $body) {
                Invoke-RestMethod -Uri $url -Method Post -Body $body `
                    -ContentType 'application/json' `
                    -Headers @{ 'X-Drover-Session' = $sid; 'X-Drover-Kind' = 'statusline' } `
                    -TimeoutSec 2 | Out-Null
            }
        } catch { }
        Write-Output ''
        exit 0
        """;

    // Events we wire. Matcher is omitted (= match all). PreToolUse/PostToolUse
    // accept a matcher pattern; leaving it unset is the documented "all tools".
    private static readonly string[] EventNames =
    {
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "Notification",
        "Stop",
        "SubagentStop",
        "SessionStart",
        "SessionEnd",
    };

    public string ScriptPath { get; }
    public string StatusScriptPath { get; }
    public string SettingsPath { get; }
    public string StatusBackupPath { get; }

    public HooksInstaller()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var hooksDir = Path.Combine(appData, "Drover", "hooks");
        Directory.CreateDirectory(hooksDir);
        ScriptPath = Path.Combine(hooksDir, "drover-hook.ps1");
        StatusScriptPath = Path.Combine(hooksDir, "drover-statusline.ps1");
        StatusBackupPath = Path.Combine(appData, "Drover", "statusline-backup.json");

        var home = Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        SettingsPath = Path.Combine(home, ".claude", "settings.json");
    }

    /// <summary>
    /// Reconciles Drover's entries in <c>~/.claude/settings.json</c> against the
    /// supplied flags. When <paramref name="hooksEnabled"/> is false, all Drover
    /// hook entries are stripped and statusLine takeover is undone regardless of
    /// <paramref name="takeOverStatusLine"/>. When <paramref name="idleHookEnabled"/>
    /// is false, the <c>Stop</c> event entry is removed (other events stay).
    /// </summary>
    public bool TryInstall(bool takeOverStatusLine, bool hooksEnabled = true, bool idleHookEnabled = true)
    {
        try
        {
            // statusLine takeover and event hooks are independent integrations —
            // each writes its own forwarder script only when the corresponding
            // setting is on.
            if (hooksEnabled) EnsureScript();
            if (takeOverStatusLine) EnsureStatusScript();
            MergeIntoFile(SettingsPath, requireParentDir: true, takeOverStatusLine, hooksEnabled, idleHookEnabled);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Installs Drover hooks into <c>&lt;project&gt;/.claude/settings.local.json</c>.
    /// Per-project install is scoped to that project (Claude reads settings.local.json
    /// in addition to global) and is the conventional place for un-committed local
    /// overrides. Returns true on success.
    /// </summary>
    public bool TryInstallProject(string projectPath, bool hooksEnabled = true, bool idleHookEnabled = true)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return false;
        try
        {
            // Per-project install only carries event hooks — the statusLine slot
            // lives in the global settings file. Status forwarder script isn't
            // needed here.
            if (hooksEnabled) EnsureScript();
            var dotClaude = Path.Combine(projectPath, ".claude");
            Directory.CreateDirectory(dotClaude);
            var target = Path.Combine(dotClaude, "settings.local.json");
            // Per-project install never claims the statusLine slot — the global
            // settings file owns that. Project files only carry hook entries.
            MergeIntoFile(target, requireParentDir: false, takeOverStatusLine: false, hooksEnabled, idleHookEnabled);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns true if a Drover hook entry is present in any settings file that would
    /// apply to this project: the project's settings.json / settings.local.json, or the
    /// global ~/.claude/settings.json. The check is a substring match against our
    /// script path — cheap and good enough.
    /// </summary>
    public bool IsInstalledForProject(string projectPath)
    {
        if (ContainsDroverHook(SettingsPath)) return true;
        if (string.IsNullOrWhiteSpace(projectPath)) return false;
        var dotClaude = Path.Combine(projectPath, ".claude");
        return ContainsDroverHook(Path.Combine(dotClaude, "settings.json"))
            || ContainsDroverHook(Path.Combine(dotClaude, "settings.local.json"));
    }

    private bool ContainsDroverHook(string settingsPath)
    {
        if (!File.Exists(settingsPath)) return false;
        try
        {
            var json = File.ReadAllText(settingsPath);
            // JSON serialisation escapes backslashes (C:\foo → C:\\foo) so a literal
            // substring search for ScriptPath misses on Windows. Match against both
            // forms so manually-edited (single-backslash) and serialised (double)
            // entries are both detected.
            var escaped = ScriptPath.Replace("\\", "\\\\");
            return json.Contains(ScriptPath, StringComparison.OrdinalIgnoreCase)
                || json.Contains(escaped, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void EnsureScript()
    {
        var versionMarker = $"# drover-hook v{ScriptVersion}\n";
        var desired = versionMarker + ScriptContent;

        if (File.Exists(ScriptPath))
        {
            try
            {
                var existing = File.ReadAllText(ScriptPath);
                if (existing.StartsWith(versionMarker, StringComparison.Ordinal)) return;
            }
            catch { /* fall through and rewrite */ }
        }
        AtomicFile.WriteAllText(ScriptPath, desired);
    }

    private void EnsureStatusScript()
    {
        var versionMarker = $"# drover-statusline v{StatusScriptVersion}\n";
        var desired = versionMarker + StatusScriptContent;

        if (File.Exists(StatusScriptPath))
        {
            try
            {
                var existing = File.ReadAllText(StatusScriptPath);
                if (existing.StartsWith(versionMarker, StringComparison.Ordinal)) return;
            }
            catch { /* fall through and rewrite */ }
        }
        AtomicFile.WriteAllText(StatusScriptPath, desired);
    }

    /// <summary>
    /// Merges Drover hook entries into a Claude settings JSON file. Creates the file
    /// (and parent dir, if not <paramref name="requireParentDir"/>) when missing.
    /// Bails silently on malformed JSON to avoid clobbering a file the user owns.
    /// </summary>
    private void MergeIntoFile(string path, bool requireParentDir, bool takeOverStatusLine, bool hooksEnabled, bool idleHookEnabled)
    {
        var dir = Path.GetDirectoryName(path)!;
        if (!Directory.Exists(dir))
        {
            if (requireParentDir) return;
            // When everything is off, don't materialise a directory just to write a no-op file.
            if (!hooksEnabled && !takeOverStatusLine && !File.Exists(path)) return;
            Directory.CreateDirectory(dir);
        }

        JsonNode root;
        if (File.Exists(path))
        {
            var raw = File.ReadAllText(path);
            try
            {
                root = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
                       ?? new JsonObject();
            }
            catch
            {
                return; // malformed — leave it alone
            }
        }
        else
        {
            // Nothing to clean up when everything is off and the file doesn't exist yet.
            if (!hooksEnabled && !takeOverStatusLine) return;
            root = new JsonObject();
        }

        if (root is not JsonObject rootObj) return;

        var hooks = rootObj["hooks"] as JsonObject;
        if (hooks is null && hooksEnabled)
        {
            hooks = new JsonObject();
            rootObj["hooks"] = hooks;
        }

        if (hooks is not null)
        {
            var command = BuildCommand(ScriptPath);
            foreach (var evt in EventNames)
            {
                // Stop is the "idle" event — gated by the idle-hook switch.
                var keep = hooksEnabled && (evt != "Stop" || idleHookEnabled);
                if (keep) UpsertEvent(hooks, evt, command);
                else RemoveEvent(hooks, evt);
            }
            // Drop an empty hooks block so disabling doesn't leave clutter.
            if (hooks.Count == 0) rootObj.Remove("hooks");
        }

        // statusLine takeover is independent of event hooks — it has its own
        // forwarder and slot in CC's settings.
        UpsertStatusLine(rootObj, BuildCommand(StatusScriptPath), takeOverStatusLine);

        AtomicFile.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>
    /// Resolves the <c>statusLine</c> slot at the root of the settings document
    /// against the <paramref name="takeOver"/> setting. CC supports a single
    /// statusLine command per settings file, so claiming it is destructive to
    /// any existing tool (e.g. ccstatusline) — gated behind a user setting.
    ///
    /// Behaviour matrix:
    /// <list type="bullet">
    /// <item><b>takeOver=true, slot empty:</b> install ours.</item>
    /// <item><b>takeOver=true, slot is ours:</b> rewrite (refreshes paths/refreshInterval).</item>
    /// <item><b>takeOver=true, slot is third-party:</b> back up to <see cref="StatusBackupPath"/>, install ours.</item>
    /// <item><b>takeOver=false, slot is ours:</b> restore from backup if present, else remove.</item>
    /// <item><b>takeOver=false, slot is third-party or empty:</b> leave alone.</item>
    /// </list>
    /// </summary>
    private void UpsertStatusLine(JsonObject root, string command, bool takeOver)
    {
        var existing = root["statusLine"] as JsonObject;
        var existingCmd = existing?["command"]?.GetValue<string>();
        var isOurs = !string.IsNullOrEmpty(existingCmd)
                     && existingCmd!.Contains(StatusScriptPath, StringComparison.OrdinalIgnoreCase);

        if (takeOver)
        {
            if (existing is not null && !isOurs)
            {
                // First-time takeover from a third-party entry — stash it so
                // toggling off can restore it. Don't overwrite an existing
                // backup; the *first* third-party value is the one to preserve.
                if (!File.Exists(StatusBackupPath))
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(StatusBackupPath)!);
                        AtomicFile.WriteAllText(StatusBackupPath,
                            existing.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
                    }
                    catch { /* non-fatal — worst case the user re-installs ccstatusline manually */ }
                }
            }

            root["statusLine"] = new JsonObject
            {
                ["type"] = "command",
                ["command"] = command,
                ["padding"] = 0,
                ["refreshInterval"] = 10,
            };
            return;
        }

        // takeOver = false. Only act if the existing entry is ours; otherwise
        // the user is using something else and we mustn't touch it.
        if (!isOurs) return;

        if (File.Exists(StatusBackupPath))
        {
            try
            {
                var backupRaw = File.ReadAllText(StatusBackupPath);
                var restored = JsonNode.Parse(backupRaw) as JsonObject;
                if (restored is not null)
                {
                    root["statusLine"] = restored;
                    File.Delete(StatusBackupPath);
                    return;
                }
            }
            catch { /* malformed backup — fall through to removal */ }
        }

        root.Remove("statusLine");
    }

    /// <summary>
    /// Ensures the named event has exactly one Drover entry pointing at our script.
    /// Strips any prior Drover entries (recognised by the script path appearing in
    /// the command) before adding a fresh one, leaving user-added entries untouched.
    /// </summary>
    private void UpsertEvent(JsonObject hooks, string eventName, string command)
    {
        var arr = hooks[eventName] as JsonArray;
        if (arr is null)
        {
            arr = new JsonArray();
            hooks[eventName] = arr;
        }

        for (int i = arr.Count - 1; i >= 0; i--)
        {
            if (IsDroverEntry(arr[i])) arr.RemoveAt(i);
        }

        arr.Add(new JsonObject
        {
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                },
            },
        });
    }

    /// <summary>
    /// Strips Drover entries from the named event's array. Removes the event key
    /// entirely if no entries remain — keeps the file tidy when toggling off.
    /// </summary>
    private void RemoveEvent(JsonObject hooks, string eventName)
    {
        if (hooks[eventName] is not JsonArray arr) return;
        for (int i = arr.Count - 1; i >= 0; i--)
        {
            if (IsDroverEntry(arr[i])) arr.RemoveAt(i);
        }
        if (arr.Count == 0) hooks.Remove(eventName);
    }

    private bool IsDroverEntry(JsonNode? node)
    {
        if (node is not JsonObject obj) return false;
        if (obj["hooks"] is not JsonArray inner) return false;
        foreach (var h in inner)
        {
            if (h is not JsonObject ho) continue;
            var cmd = ho["command"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(cmd) && cmd!.Contains(ScriptPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static string BuildCommand(string scriptPath)
    {
        // Quote the path so spaces in %APPDATA% (rare, but possible) don't break it.
        // -NoProfile keeps invocation fast; -File runs the script and exits.
        return $"pwsh.exe -NoLogo -NoProfile -File \"{scriptPath}\"";
    }
}
