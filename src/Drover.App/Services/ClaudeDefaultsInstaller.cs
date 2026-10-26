using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Drover.App.Services;

/// <summary>
/// Seeds a project's <c>.claude/settings.local.json</c> with Drover's default
/// Claude Code settings: the <c>env</c> block and the <c>permissions</c>
/// allow/deny lists from the embedded <c>Templates/claude-default-settings.json</c>.
///
/// Merge-only, never destructive: env keys are added only when absent, and
/// allow/deny entries are appended only when not already present, so user
/// customisations survive re-runs. Hooks and statusLine are deliberately not
/// handled here — <see cref="HooksInstaller.TryInstallProject"/> owns those and
/// callers should invoke both to produce the full default file.
/// </summary>
public sealed class ClaudeDefaultsInstaller
{
    private const string TemplateName = "claude-default-settings.json";
    private const string ResourcePrefix = "Drover.App.Templates.";

    /// <summary>
    /// Deny entries earlier templates shipped that Claude Code now rejects at
    /// startup ("Deny rule Write(...) never matches · use Edit(...)"). Stripped
    /// on merge so re-running the installer heals already-seeded projects.
    /// </summary>
    private static readonly string[] RetiredDenyEntries =
    [
        "Write(~/.ssh/*)",
        "Write(~/.gnupg/*)",
        "Write(~/.aws/*)",
    ];

    /// <summary>
    /// Merges the default env/permissions into
    /// <c>&lt;project&gt;/.claude/settings.local.json</c>, creating the file and
    /// <c>.claude/</c> directory when missing. Returns false on failure or when
    /// the existing file is malformed JSON (which is left untouched).
    /// </summary>
    public bool TryInstallProject(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath) || !Directory.Exists(projectPath)) return false;
        try
        {
            JsonObject? defaults;
            using (var stream = typeof(ClaudeDefaultsInstaller).Assembly
                       .GetManifestResourceStream(ResourcePrefix + TemplateName))
            {
                if (stream is null) return false;
                defaults = JsonNode.Parse(stream) as JsonObject;
            }
            if (defaults is null) return false;

            var dotClaude = Path.Combine(projectPath, ".claude");
            Directory.CreateDirectory(dotClaude);
            var target = Path.Combine(dotClaude, "settings.local.json");

            JsonNode root;
            if (File.Exists(target))
            {
                try
                {
                    root = JsonNode.Parse(File.ReadAllText(target),
                               documentOptions: new JsonDocumentOptions
                               {
                                   CommentHandling = JsonCommentHandling.Skip,
                                   AllowTrailingCommas = true,
                               })
                           ?? new JsonObject();
                }
                catch
                {
                    return false; // malformed — leave the user's file alone
                }
            }
            else
            {
                root = new JsonObject();
            }

            if (root is not JsonObject rootObj) return false;

            MergeEnv(rootObj, defaults["env"] as JsonObject);
            MergePermissions(rootObj, defaults["permissions"] as JsonObject);

            AtomicFile.WriteAllText(target, rootObj.ToJsonString(SettingsJson.Indented));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Adds default env keys that aren't already set. Existing values win.</summary>
    private static void MergeEnv(JsonObject root, JsonObject? defaults)
    {
        if (defaults is null) return;
        if (root["env"] is not JsonObject env)
        {
            env = new JsonObject();
            root["env"] = env;
        }
        foreach (var (key, value) in defaults)
        {
            if (!env.ContainsKey(key))
                env[key] = value?.DeepClone();
        }
    }

    /// <summary>Appends default allow/deny entries missing from the existing lists.</summary>
    private static void MergePermissions(JsonObject root, JsonObject? defaults)
    {
        if (defaults is null) return;
        if (root["permissions"] is not JsonObject perms)
        {
            perms = new JsonObject();
            root["permissions"] = perms;
        }
        MergeStringList(perms, defaults, "allow");
        MergeStringList(perms, defaults, "deny");

        if (perms["deny"] is JsonArray deny)
        {
            for (var i = deny.Count - 1; i >= 0; i--)
            {
                if (deny[i] is JsonValue jv
                    && jv.TryGetValue<string>(out var s)
                    && Array.IndexOf(RetiredDenyEntries, s) >= 0)
                    deny.RemoveAt(i);
            }
        }
    }

    private static void MergeStringList(JsonObject perms, JsonObject defaults, string key)
    {
        if (defaults[key] is not JsonArray source) return;
        if (perms[key] is not JsonArray target)
        {
            target = new JsonArray();
            perms[key] = target;
        }
        foreach (var node in source)
        {
            var value = (node as JsonValue)?.GetValue<string>();
            if (string.IsNullOrEmpty(value)) continue;
            if (!Contains(target, value)) target.Add(value);
        }
    }

    private static bool Contains(JsonArray array, string value)
    {
        foreach (var node in array)
        {
            if (node is JsonValue jv
                && jv.TryGetValue<string>(out var s)
                && string.Equals(s, value, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
