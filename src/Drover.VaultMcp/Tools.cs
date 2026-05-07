using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using Drover.Vault.Core;
using Drover.Vault.Core.Models;

namespace Drover.VaultMcp;

// Five tool handlers — vault_list / vault_read / vault_write /
// vault_append / vault_search. Each takes the supplied arguments
// JsonElement and the active project root (null when unconfigured),
// returns a ToolsCallResult content block. Path containment is
// enforced inside Drover.Vault.Core; this layer translates failures
// into JSON-RPC InvalidParams errors.
internal static class Tools
{
    public static IReadOnlyList<ToolDefinition> Definitions { get; } = new[]
    {
        new ToolDefinition
        {
            Name = "vault_list",
            Description = "List markdown notes in a vault namespace.",
            InputSchema = ParseSchema("""
            {
              "type": "object",
              "properties": {
                "scope": { "type": "string", "enum": ["project", "global"] },
                "folder": { "type": "string" }
              },
              "required": ["scope"]
            }
            """),
        },
        new ToolDefinition
        {
            Name = "vault_read",
            Description = "Read the contents of a markdown note.",
            InputSchema = ParseSchema("""
            {
              "type": "object",
              "properties": {
                "scope": { "type": "string", "enum": ["project", "global"] },
                "path":  { "type": "string" }
              },
              "required": ["scope", "path"]
            }
            """),
        },
        new ToolDefinition
        {
            Name = "vault_write",
            Description = "Write a markdown note (atomic temp+rename, creates parent dirs).",
            InputSchema = ParseSchema("""
            {
              "type": "object",
              "properties": {
                "scope":   { "type": "string", "enum": ["project", "global"] },
                "path":    { "type": "string" },
                "content": { "type": "string" }
              },
              "required": ["scope", "path", "content"]
            }
            """),
        },
        new ToolDefinition
        {
            Name = "vault_append",
            Description = "Append text to a markdown note (single leading newline if needed).",
            InputSchema = ParseSchema("""
            {
              "type": "object",
              "properties": {
                "scope":   { "type": "string", "enum": ["project", "global"] },
                "path":    { "type": "string" },
                "content": { "type": "string" }
              },
              "required": ["scope", "path", "content"]
            }
            """),
        },
        new ToolDefinition
        {
            Name = "vault_search",
            Description = "Case-insensitive substring search across a vault namespace.",
            InputSchema = ParseSchema("""
            {
              "type": "object",
              "properties": {
                "scope": { "type": "string", "enum": ["project", "global"] },
                "query": { "type": "string" },
                "max":   { "type": "integer", "minimum": 1, "maximum": 200 }
              },
              "required": ["scope", "query"]
            }
            """),
        },
    };

    public static ToolsCallResult Call(string name, JsonElement arguments, string? projectRoot)
    {
        var scope = ParseScope(arguments);
        var root = ResolveRoot(scope, projectRoot);

        switch (name)
        {
            case "vault_list":
            {
                var folder = OptionalString(arguments, "folder");
                var entries = VaultIo.List(root, folder);
                return Json(BuildListPayload(entries));
            }
            case "vault_read":
            {
                var path = RequiredString(arguments, "path");
                string? content = null;
                try { content = VaultIo.Read(root, path); }
                catch (System.IO.FileNotFoundException) { content = null; }
                catch (System.IO.DirectoryNotFoundException) { content = null; }
                return Json(new JsonObject { ["content"] = content });
            }
            case "vault_write":
            {
                var path = RequiredString(arguments, "path");
                var content = RequiredString(arguments, "content");
                VaultIo.Write(root, path, content);
                return Json(new JsonObject { ["ok"] = true });
            }
            case "vault_append":
            {
                var path = RequiredString(arguments, "path");
                var content = RequiredString(arguments, "content");
                VaultIo.Append(root, path, content);
                return Json(new JsonObject { ["ok"] = true });
            }
            case "vault_search":
            {
                var query = RequiredString(arguments, "query");
                var max = OptionalInt(arguments, "max") ?? 50;
                var hits = VaultIo.Search(root, query, max);
                var arr = new JsonArray();
                foreach (var h in hits)
                {
                    arr.Add(new JsonObject
                    {
                        ["path"] = h.RelativePath,
                        ["line"] = h.LineNumber,
                        ["snippet"] = h.Snippet,
                    });
                }
                return Json(arr);
            }
            default:
                throw new McpToolException(JsonRpcError.MethodNotFound, $"Unknown tool: {name}");
        }
    }

    private static JsonArray BuildListPayload(IReadOnlyList<VaultEntry> entries)
    {
        var arr = new JsonArray();
        foreach (var e in entries)
        {
            arr.Add(new JsonObject
            {
                ["path"] = e.RelativePath,
                ["modified"] = e.LastModifiedUtc.ToString("o"),
                ["bytes"] = e.Bytes,
                ["summary"] = e.Summary,
            });
        }
        return arr;
    }

    private static ToolsCallResult Json(JsonNode payload)
    {
        return new ToolsCallResult
        {
            Content = new[]
            {
                new ToolContent
                {
                    Type = "text",
                    Text = payload.ToJsonString(JsonOpts.Default),
                },
            },
        };
    }

    private static VaultScope ParseScope(JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object || !arguments.TryGetProperty("scope", out var scopeEl))
            throw new McpToolException(JsonRpcError.InvalidParams, "Missing required 'scope'.");
        var s = scopeEl.GetString();
        return s switch
        {
            "project" => VaultScope.Project,
            "global"  => VaultScope.Global,
            _ => throw new McpToolException(JsonRpcError.InvalidParams, "scope must be 'project' or 'global'."),
        };
    }

    private static string ResolveRoot(VaultScope scope, string? projectRoot)
    {
        if (scope == VaultScope.Global) return VaultPaths.GetGlobalRoot();
        if (string.IsNullOrEmpty(projectRoot))
            throw new McpToolException(JsonRpcError.InvalidParams, "project namespace not configured (launch with --project <path>).");
        return VaultPaths.GetProjectRoot(projectRoot);
    }

    private static string RequiredString(JsonElement args, string name)
    {
        if (!args.TryGetProperty(name, out var el) || el.ValueKind != JsonValueKind.String)
            throw new McpToolException(JsonRpcError.InvalidParams, $"Missing required string '{name}'.");
        return el.GetString() ?? string.Empty;
    }

    private static string? OptionalString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (!args.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static int? OptionalInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object) return null;
        if (!args.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v) ? v : null;
    }

    private static JsonNode ParseSchema(string json) => JsonNode.Parse(json) ?? new JsonObject();
}

internal sealed class McpToolException : Exception
{
    public int Code { get; }
    public McpToolException(int code, string message) : base(message) { Code = code; }
}
