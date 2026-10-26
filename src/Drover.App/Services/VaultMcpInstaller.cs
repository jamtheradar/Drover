using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Drover.App.Services;

// Writes the `drover-vault` MCP server entry into a project's
// `<project>/.mcp.json` so any Claude Code session in that project can
// reach the vault via the Drover.VaultMcp.exe built in spec 0005.
//
// Idempotent merge: an existing `.mcp.json` is parsed and only the
// `mcpServers.drover-vault` key is replaced — sibling entries
// (`playwright`, etc.) survive verbatim. Atomic write via AtomicFile so
// a crash mid-write leaves either the prior file or the new file, never
// a partial.
public sealed class VaultMcpInstaller
{
    public const string ServerName = "drover-vault";
    public const string McpFileName = ".mcp.json";

    /// <summary>
    /// Returns true iff the project's <c>.mcp.json</c> exists, parses,
    /// and contains an <c>mcpServers.drover-vault</c> entry whose
    /// <c>command</c> matches <see cref="GetMcpExePath"/> and whose
    /// <c>args[1]</c> matches <paramref name="projectPath"/>. Used by
    /// the menu/toast logic to distinguish first install from re-install.
    /// </summary>
    public bool IsInstalled(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath)) return false;
        var mcpPath = Path.Combine(projectPath, McpFileName);
        if (!File.Exists(mcpPath)) return false;

        try
        {
            var node = JsonNode.Parse(File.ReadAllText(mcpPath));
            var entry = node?["mcpServers"]?[ServerName];
            if (entry is not JsonObject obj) return false;

            var command = obj["command"]?.GetValue<string>();
            if (!string.Equals(command, GetMcpExePath(), StringComparison.OrdinalIgnoreCase)) return false;

            if (obj["args"] is not JsonArray args || args.Count < 2) return false;
            return string.Equals(args[1]?.GetValue<string>(), projectPath, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Result of a successful <see cref="Install"/>. <c>WasFresh</c> is
    /// true when no <c>.mcp.json</c> existed before the call;
    /// <c>WasUpdated</c> is true when the file existed and the
    /// <c>drover-vault</c> entry was changed (or added). The caller uses
    /// these to pick the right toast wording.
    /// </summary>
    public readonly record struct InstallResult(bool WasFresh, bool WasUpdated);

    public InstallResult Install(string projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
            throw new ArgumentException("Project path must be non-empty.", nameof(projectPath));

        var exe = GetMcpExePath();
        if (!File.Exists(exe))
            throw new FileNotFoundException(
                $"Vault MCP server not found at {exe}; build the solution and retry.", exe);

        var mcpPath = Path.Combine(projectPath, McpFileName);
        bool wasFresh = !File.Exists(mcpPath);

        // Read-merge-write through JsonNode so we touch only the
        // drover-vault entry. JsonObject preserves insertion order, so
        // pre-existing entries land in the same place they started.
        JsonObject root;
        if (wasFresh)
        {
            root = new JsonObject();
        }
        else
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(mcpPath)) as JsonObject
                       ?? new JsonObject();
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException(
                    $"Existing .mcp.json is not valid JSON: {ex.Message}", ex);
            }
        }

        if (root["mcpServers"] is not JsonObject servers)
        {
            servers = new JsonObject();
            root["mcpServers"] = servers;
        }

        var newEntry = new JsonObject
        {
            ["command"] = exe,
            ["args"] = new JsonArray("--project", projectPath),
        };

        bool wasUpdated = !JsonNode.DeepEquals(servers[ServerName], newEntry);

        // Detach the previous entry first so JsonNode doesn't throw
        // ArgumentException("already has a parent") when we attach the
        // newly-built JsonObject under the same key.
        servers.Remove(ServerName);
        servers[ServerName] = newEntry;

        var json = root.ToJsonString(SettingsJson.Indented);
        AtomicFile.WriteAllText(mcpPath, json);

        return new InstallResult(wasFresh, wasUpdated);
    }

    /// <summary>
    /// Resolves the MCP exe path next to <c>Drover.App.exe</c>. Per the
    /// build wiring in spec 0005, the four MCP runtime files live in
    /// <c>vaultmcp\</c> alongside the host exe. We anchor on the
    /// running <c>Drover.App.dll</c>'s own location rather than
    /// <see cref="System.AppContext.BaseDirectory"/> so the resolution
    /// is the same whether the assembly is hosted by Drover.App.exe,
    /// a unit-test runner, or a reflection-based smoke harness.
    /// </summary>
    public string GetMcpExePath()
    {
        var asmPath = typeof(VaultMcpInstaller).Assembly.Location;
        var asmDir = Path.GetDirectoryName(asmPath) ?? System.AppContext.BaseDirectory;
        return Path.Combine(asmDir, "vaultmcp", "Drover.VaultMcp.exe");
    }
}
