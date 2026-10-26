using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Drover.App.Services;

/// <summary>
/// JSON + TOML helpers for the Codex hooks installer. Lives separately from
/// <see cref="CodexHooksInstaller"/> so the file-shape logic can be reasoned
/// about in isolation from the file-path/script-content concerns.
///
/// JSON path (<c>hooks.json</c>): idempotent upsert that strips any prior
/// Drover entries (matched by exact script-path substring in the command
/// string) before adding a fresh one, leaving user-added entries untouched.
///
/// TOML path (<c>config.toml</c>): minimal read/modify/write. We only know
/// about one section and one key (<c>[features] hooks = true</c>); the rest
/// of the file is round-tripped line-by-line. Not a general TOML parser — if
/// Codex's schema grows beyond this single flag, swap in a real parser.
/// </summary>
internal static class CodexHookSettingsMerger
{
    /// <summary>
    /// Event names Drover wires for Codex. Identical to the spec's set —
    /// SessionStart / UserPromptSubmit / PreToolUse / PostToolUse /
    /// PermissionRequest / Stop.
    /// </summary>
    public static readonly string[] EventNames =
    {
        "SessionStart",
        "UserPromptSubmit",
        "PreToolUse",
        "PostToolUse",
        "PermissionRequest",
        "Stop",
    };

    /// <summary>
    /// Reads <paramref name="path"/> if it exists (creates an empty object
    /// otherwise), upserts Drover's six event entries (each pointing at
    /// <paramref name="command"/>), and atomically writes the result.
    /// Pre-existing non-Drover entries under any event survive.
    ///
    /// Drover entries are identified by exact substring match of
    /// <paramref name="scriptPath"/> in the inner <c>command</c> field — same
    /// recognition scheme <see cref="HooksInstaller"/> uses for Claude.
    /// </summary>
    public static void UpsertHooksJson(string path, string scriptPath, string command)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        JsonNode root;
        if (File.Exists(path))
        {
            var raw = File.ReadAllText(path);
            try
            {
                root = JsonNode.Parse(raw, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true,
                }) ?? new JsonObject();
            }
            catch
            {
                // Malformed user JSON — bail rather than clobber.
                return;
            }
        }
        else
        {
            root = new JsonObject();
        }

        if (root is not JsonObject rootObj) return;

        var hooks = rootObj["hooks"] as JsonObject;
        if (hooks is null)
        {
            hooks = new JsonObject();
            rootObj["hooks"] = hooks;
        }

        foreach (var evt in EventNames)
            UpsertEvent(hooks, evt, scriptPath, command);

        AtomicFile.WriteAllText(path,
            root.ToJsonString(SettingsJson.Indented));
    }

    /// <summary>
    /// Strips Drover's entries (by script-path substring) from every event
    /// array under <c>hooks</c>. Removes emptied event keys and an emptied
    /// <c>hooks</c> object so the file stays tidy. Leaves non-Drover entries
    /// and the rest of the file alone. No-op if the file doesn't exist.
    /// </summary>
    public static void RemoveDroverEntriesFromHooksJson(string path, string scriptPath)
    {
        if (!File.Exists(path)) return;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch
        {
            return;
        }
        if (root is not JsonObject rootObj) return;
        if (rootObj["hooks"] is not JsonObject hooks) return;

        var keys = new List<string>();
        foreach (var kv in hooks) keys.Add(kv.Key);
        foreach (var key in keys)
        {
            if (hooks[key] is not JsonArray arr) continue;
            for (int i = arr.Count - 1; i >= 0; i--)
                if (IsDroverEntry(arr[i], scriptPath)) arr.RemoveAt(i);
            if (arr.Count == 0) hooks.Remove(key);
        }
        if (hooks.Count == 0) rootObj.Remove("hooks");

        AtomicFile.WriteAllText(path,
            root.ToJsonString(SettingsJson.Indented));
    }

    private static void UpsertEvent(JsonObject hooks, string eventName, string scriptPath, string command)
    {
        var arr = hooks[eventName] as JsonArray;
        if (arr is null)
        {
            arr = new JsonArray();
            hooks[eventName] = arr;
        }

        for (int i = arr.Count - 1; i >= 0; i--)
            if (IsDroverEntry(arr[i], scriptPath)) arr.RemoveAt(i);

        arr.Add(new JsonObject
        {
            ["matcher"] = "",
            ["hooks"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "command",
                    ["command"] = command,
                    ["timeout"] = 5,
                },
            },
        });
    }

    private static bool IsDroverEntry(JsonNode? node, string scriptPath)
    {
        if (node is not JsonObject obj) return false;
        if (obj["hooks"] is not JsonArray inner) return false;
        foreach (var h in inner)
        {
            if (h is not JsonObject ho) continue;
            var cmd = ho["command"]?.GetValue<string>();
            if (!string.IsNullOrEmpty(cmd)
                && cmd!.Contains(scriptPath, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Ensures <c>[features] {keyName} = true</c> exists in the TOML file at
    /// <paramref name="path"/>. Creates the file if missing. If the file
    /// exists, walks it line-by-line: when a <c>[features]</c> section is
    /// found, sets or inserts <c>{keyName} = true</c> within it; otherwise
    /// appends a new <c>[features]</c> section at the end. Any keys listed in
    /// <paramref name="deprecatedKeys"/> are stripped from the <c>[features]</c>
    /// section so renames migrate cleanly. All other content is preserved
    /// verbatim.
    /// </summary>
    public static void EnsureFeatureFlagToml(
        string path,
        string keyName,
        IReadOnlyList<string>? deprecatedKeys = null)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);

        if (!File.Exists(path))
        {
            AtomicFile.WriteAllText(path, $"[features]\n{keyName} = true\n");
            return;
        }

        var raw = File.ReadAllText(path);
        // Preserve the file's existing newline style so a CRLF-authored
        // config doesn't get its line endings flipped on every Drover launch.
        var newline = raw.Contains("\r\n") ? "\r\n" : "\n";
        var lines = new List<string>(raw.Split('\n'));
        // Drop the trailing empty element introduced by a trailing newline so
        // we don't append after an "empty" line. We'll re-add the trailing
        // newline at write time.
        if (lines.Count > 0 && lines[lines.Count - 1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        // Strip any '\r' left over from CRLF input so logic below compares
        // cleanly; we'll re-emit using `newline`.
        for (int i = 0; i < lines.Count; i++)
            if (lines[i].EndsWith('\r')) lines[i] = lines[i][..^1];

        int featuresStart = -1;
        int featuresEnd = lines.Count; // end of file by default
        for (int i = 0; i < lines.Count; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (featuresStart == -1)
            {
                if (IsSectionHeader(trimmed, "features"))
                    featuresStart = i;
            }
            else
            {
                if (trimmed.StartsWith('[') && trimmed.EndsWith(']'))
                {
                    featuresEnd = i;
                    break;
                }
            }
        }

        if (featuresStart == -1)
        {
            // No [features] section — append. Ensure a blank line separates
            // it from prior content if the file doesn't already end in one.
            if (lines.Count > 0 && lines[^1].Trim().Length > 0)
                lines.Add(string.Empty);
            lines.Add("[features]");
            lines.Add($"{keyName} = true");
        }
        else
        {
            // Strip deprecated keys first so subsequent indices stay stable.
            if (deprecatedKeys is { Count: > 0 })
            {
                for (int i = featuresEnd - 1; i > featuresStart; i--)
                {
                    var trimmed = lines[i].TrimStart();
                    if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                    var eq = trimmed.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = trimmed[..eq].TrimEnd();
                    foreach (var dep in deprecatedKeys)
                    {
                        if (string.Equals(k, dep, StringComparison.Ordinal))
                        {
                            lines.RemoveAt(i);
                            featuresEnd--;
                            break;
                        }
                    }
                }
            }

            int keyLine = -1;
            for (int i = featuresStart + 1; i < featuresEnd; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
                var eq = trimmed.IndexOf('=');
                if (eq <= 0) continue;
                var k = trimmed[..eq].TrimEnd();
                if (string.Equals(k, keyName, StringComparison.Ordinal))
                {
                    keyLine = i;
                    break;
                }
            }
            if (keyLine >= 0)
            {
                lines[keyLine] = $"{keyName} = true";
            }
            else
            {
                // Insert immediately after the section header so the new key
                // is unambiguously inside [features].
                lines.Insert(featuresStart + 1, $"{keyName} = true");
            }
        }

        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(lines[i]);
            sb.Append(newline);
        }
        AtomicFile.WriteAllText(path, sb.ToString());
    }

    private static bool IsSectionHeader(string trimmed, string name)
    {
        if (!trimmed.StartsWith('[') || !trimmed.EndsWith(']')) return false;
        var inner = trimmed[1..^1].Trim();
        return string.Equals(inner, name, StringComparison.OrdinalIgnoreCase);
    }
}
