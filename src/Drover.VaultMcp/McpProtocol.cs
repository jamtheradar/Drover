using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Drover.VaultMcp;

// JSON-RPC 2.0 + MCP DTOs. Hand-rolled because the protocol surface
// the vault server uses is small (initialize, notifications/initialized,
// tools/list, tools/call) and pulling in an SDK would dwarf the
// per-request work the handlers actually do.
//
// Field names match the MCP stdio transport spec verbatim — every
// property carries an explicit [JsonPropertyName] so the canonical
// camelCase wire form survives any default-naming-policy churn.
internal static class JsonOpts
{
    public static readonly JsonSerializerOptions Default = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}

internal sealed class JsonRpcRequest
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")]      public JsonElement? Id { get; set; }
    [JsonPropertyName("method")]  public string Method { get; set; } = string.Empty;
    [JsonPropertyName("params")]  public JsonElement? Params { get; set; }
}

internal sealed class JsonRpcResponse
{
    [JsonPropertyName("jsonrpc")] public string JsonRpc { get; set; } = "2.0";
    [JsonPropertyName("id")]      public JsonElement? Id { get; set; }
    [JsonPropertyName("result")]  public object? Result { get; set; }
    [JsonPropertyName("error")]   public JsonRpcError? Error { get; set; }
}

internal sealed class JsonRpcError
{
    [JsonPropertyName("code")]    public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = string.Empty;
    [JsonPropertyName("data")]    public object? Data { get; set; }

    public const int ParseError      = -32700;
    public const int InvalidRequest  = -32600;
    public const int MethodNotFound  = -32601;
    public const int InvalidParams   = -32602;
    public const int InternalError   = -32603;
}

internal sealed class InitializeResult
{
    [JsonPropertyName("protocolVersion")] public string ProtocolVersion { get; set; } = "2024-11-05";
    [JsonPropertyName("capabilities")]    public Capabilities Capabilities { get; set; } = new();
    [JsonPropertyName("serverInfo")]      public ServerInfo ServerInfo { get; set; } = new();
}

internal sealed class Capabilities
{
    // Empty object → "tools" capability advertised with no extra
    // sub-flags. MCP clients treat the presence of the "tools" key
    // as the signal; an empty body is the spec-compliant minimum.
    [JsonPropertyName("tools")] public JsonObject Tools { get; set; } = new();
}

internal sealed class ServerInfo
{
    [JsonPropertyName("name")]    public string Name { get; set; } = "drover-vault";
    [JsonPropertyName("version")] public string Version { get; set; } = "0.1.0";
}

internal sealed class ToolsListResult
{
    [JsonPropertyName("tools")] public ToolDefinition[] Tools { get; set; } = System.Array.Empty<ToolDefinition>();
}

internal sealed class ToolDefinition
{
    [JsonPropertyName("name")]        public string Name { get; set; } = string.Empty;
    [JsonPropertyName("description")] public string Description { get; set; } = string.Empty;
    [JsonPropertyName("inputSchema")] public JsonNode InputSchema { get; set; } = new JsonObject();
}

internal sealed class ToolsCallResult
{
    [JsonPropertyName("content")] public ToolContent[] Content { get; set; } = System.Array.Empty<ToolContent>();
    [JsonPropertyName("isError")] public bool? IsError { get; set; }
}

internal sealed class ToolContent
{
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("text")] public string Text { get; set; } = string.Empty;
}
