using System;
using System.IO;
using System.Text;
using System.Text.Json;
using Drover.Vault.Core;

namespace Drover.VaultMcp;

// Newline-delimited JSON-RPC 2.0 over stdio, per the MCP stdio
// transport. One JSON object per line on stdin → one JSON object per
// line on stdout. Stdout is reserved for protocol; everything else
// (logs, traces, errors) is funnelled through Logging which mirrors
// to stderr + the rotating file at %APPDATA%\Drover\logs\vaultmcp.log.
internal sealed class JsonRpcServer
{
    private readonly string? _projectRoot;
    private readonly TextReader _stdin;
    private readonly TextWriter _stdout;

    public JsonRpcServer(string? projectRoot, TextReader stdin, TextWriter stdout)
    {
        _projectRoot = projectRoot;
        _stdin = stdin;
        _stdout = stdout;
    }

    // Returns the desired process exit code. 0 on EOF (clean shutdown
    // when the client closes the pipe); 1 if the read loop terminates
    // because of an unhandled host-side error.
    public int Run()
    {
        Logging.Info($"vaultmcp starting (project={_projectRoot ?? "<none>"}).");
        try
        {
            string? line;
            while ((line = _stdin.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                Handle(line);
            }
            Logging.Info("vaultmcp stdin EOF — exiting cleanly.");
            return 0;
        }
        catch (Exception ex)
        {
            Logging.Error("vaultmcp read loop crashed.", ex);
            return 1;
        }
    }

    private void Handle(string line)
    {
        JsonRpcRequest? req;
        try
        {
            req = JsonSerializer.Deserialize<JsonRpcRequest>(line, JsonOpts.Default);
        }
        catch (Exception ex)
        {
            Logging.Warn($"Parse error: {ex.Message}");
            WriteResponse(new JsonRpcResponse
            {
                Error = new JsonRpcError { Code = JsonRpcError.ParseError, Message = ex.Message },
            });
            return;
        }
        if (req is null || string.IsNullOrEmpty(req.Method))
        {
            WriteResponse(new JsonRpcResponse
            {
                Error = new JsonRpcError { Code = JsonRpcError.InvalidRequest, Message = "Empty request." },
            });
            return;
        }

        // Notifications (no `id`) get no response, by JSON-RPC spec.
        var isNotification = req.Id is null || req.Id.Value.ValueKind == JsonValueKind.Null
                                            || req.Id.Value.ValueKind == JsonValueKind.Undefined;

        try
        {
            var result = Dispatch(req);
            if (isNotification) return;
            WriteResponse(new JsonRpcResponse { Id = req.Id, Result = result });
        }
        catch (McpToolException tex)
        {
            Logging.Warn($"Tool error ({req.Method}): {tex.Message}");
            if (isNotification) return;
            WriteResponse(new JsonRpcResponse
            {
                Id = req.Id,
                Error = new JsonRpcError { Code = tex.Code, Message = tex.Message },
            });
        }
        catch (VaultPathException vex)
        {
            // Path containment failures route to InvalidParams so the
            // agent gets a precise reason without leaking a stack trace.
            Logging.Warn($"Path containment ({req.Method}): {vex.Message}");
            if (isNotification) return;
            WriteResponse(new JsonRpcResponse
            {
                Id = req.Id,
                Error = new JsonRpcError { Code = JsonRpcError.InvalidParams, Message = $"path containment: {vex.Message}" },
            });
        }
        catch (Exception ex)
        {
            Logging.Error($"Unhandled exception in {req.Method}.", ex);
            if (isNotification) return;
            WriteResponse(new JsonRpcResponse
            {
                Id = req.Id,
                Error = new JsonRpcError { Code = JsonRpcError.InternalError, Message = ex.Message },
            });
        }
    }

    private object? Dispatch(JsonRpcRequest req)
    {
        switch (req.Method)
        {
            case "initialize":
                return new InitializeResult();
            case "notifications/initialized":
                return null;  // notification — no response written
            case "tools/list":
                return new ToolsListResult { Tools = ToArrayCopy() };
            case "tools/call":
                return DispatchToolsCall(req);
            default:
                throw new McpToolException(JsonRpcError.MethodNotFound, $"Method not found: {req.Method}");
        }
    }

    private static ToolDefinition[] ToArrayCopy()
    {
        var src = Tools.Definitions;
        var arr = new ToolDefinition[src.Count];
        for (int i = 0; i < src.Count; i++) arr[i] = src[i];
        return arr;
    }

    private ToolsCallResult DispatchToolsCall(JsonRpcRequest req)
    {
        if (req.Params is not { } p || p.ValueKind != JsonValueKind.Object)
            throw new McpToolException(JsonRpcError.InvalidParams, "tools/call requires params object.");
        if (!p.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String)
            throw new McpToolException(JsonRpcError.InvalidParams, "tools/call requires 'name'.");
        var name = nameEl.GetString() ?? string.Empty;
        var args = p.TryGetProperty("arguments", out var argEl)
            ? argEl
            : default;
        return Tools.Call(name, args, _projectRoot);
    }

    private void WriteResponse(JsonRpcResponse response)
    {
        var json = JsonSerializer.Serialize(response, JsonOpts.Default);
        // Single line, single newline. Stdout is the protocol channel;
        // never write through Console.WriteLine elsewhere.
        _stdout.Write(json);
        _stdout.Write('\n');
        _stdout.Flush();
    }
}
