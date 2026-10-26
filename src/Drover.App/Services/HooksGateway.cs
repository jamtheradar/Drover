using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text.Json;

namespace Drover.App.Services;

/// <summary>
/// Loopback HTTP listener that receives agent hook events (both Claude Code and
/// Codex CLI). Each tab launches with a unique session UUID + this listener's URL
/// injected as env vars; the agent's configured hooks POST tool/permission/
/// lifecycle events back to us tagged with the session id, which we route to the
/// matching tab.
///
/// v0.2 scaffold: today this just receives and dispatches events. AttentionMonitor
/// (OSC scraping) remains the primary signal in v0.1; this listener exists so the
/// piping is in place when we wire user hook scripts in v0.2.
/// </summary>
public sealed class HooksGateway : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, Action<HookEvent>> _routes = new();
    private readonly ConcurrentDictionary<string, HookAgentKind> _routeKinds = new();
    private readonly ConcurrentDictionary<string, Action<string>> _statusRoutes = new();
    private readonly object _logLock = new();
    private readonly object _statusLogLock = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;

    public string? Url { get; private set; }
    public string LogPath { get; }
    public string StatusLogPath { get; }

    /// <summary>
    /// When false, AppendLog / AppendStatusLog are no-ops. Toggled from settings —
    /// off by default so steady-state operation doesn't fill the log directory.
    /// </summary>
    public bool DebugLogging { get; set; }

    public HooksGateway()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Drover", "logs");
        Directory.CreateDirectory(dir);
        LogPath = Path.Combine(dir, "hooks.jsonl");
        StatusLogPath = Path.Combine(dir, "statusline.jsonl");
    }

    public bool TryStart()
    {
        if (_listener.IsListening) return true;
        for (int port = 17923; port < 17923 + 50; port++)
        {
            var url = $"http://127.0.0.1:{port}/";
            try
            {
                _listener.Prefixes.Clear();
                _listener.Prefixes.Add(url);
                _listener.Start();
                Url = url;
                _cts = new CancellationTokenSource();
                _loop = Task.Run(() => Loop(_cts.Token));
                return true;
            }
            catch (HttpListenerException) { /* try next port */ }
            catch { return false; }
        }
        return false;
    }

    /// <summary>
    /// Register a hook handler for a tab's session id. The tab declares its
    /// <see cref="HookAgentKind"/> so the gateway can stamp every parsed
    /// <see cref="HookEvent"/> before dispatch. Pwsh tabs must not register —
    /// they don't run hooks.
    /// </summary>
    public void Register(string sessionId, HookAgentKind kind, Action<HookEvent> handler)
    {
        _routes[sessionId] = handler;
        _routeKinds[sessionId] = kind;
    }

    public void Unregister(string sessionId)
    {
        _routes.TryRemove(sessionId, out _);
        _routeKinds.TryRemove(sessionId, out _);
    }

    /// <summary>
    /// Registers a handler for Claude Code statusLine pushes for the given session.
    /// CC invokes the configured statusLine command on every refresh (default 10s
    /// when <c>refreshInterval</c> is set) with a rich JSON document on stdin —
    /// model, context window usage, rate limits, output style, vim mode, cost,
    /// lines added/removed, worktree info. The forwarder script POSTs that body
    /// here with <c>X-Drover-Kind: statusline</c> so we can route it without
    /// driving the AttentionState pipeline.
    /// </summary>
    public void RegisterStatus(string sessionId, Action<string> handler)
        => _statusRoutes[sessionId] = handler;

    public void UnregisterStatus(string sessionId) => _statusRoutes.TryRemove(sessionId, out _);

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch { break; }
            _ = Task.Run(() => Handle(ctx), ct);
        }
    }

    private void Handle(HttpListenerContext ctx)
    {
        try
        {
            using var reader = new StreamReader(ctx.Request.InputStream);
            var body = reader.ReadToEnd();
            var sessionId = ctx.Request.Headers["X-Drover-Session"] ?? string.Empty;
            var kind = ctx.Request.Headers["X-Drover-Kind"] ?? string.Empty;

            if (string.Equals(kind, "statusline", StringComparison.OrdinalIgnoreCase))
            {
                AppendStatusLog(sessionId, body, ctx.Request.RemoteEndPoint?.ToString());
                if (!string.IsNullOrEmpty(sessionId) && _statusRoutes.TryGetValue(sessionId, out var sHandler))
                {
                    try { sHandler(body); } catch { /* per-tab handler must not kill the gateway */ }
                }
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            // Resolve the registering tab's agent kind by session id. Unknown
            // session → drop the event (same as the previous unrouted behaviour)
            // and log a warning so a misconfigured hook install is diagnosable.
            if (string.IsNullOrEmpty(sessionId) || !_routeKinds.TryGetValue(sessionId, out var agentKind))
            {
                AppLog.Warn("HooksGateway",
                    $"Dropped hook event from unregistered session id '{sessionId}'. " +
                    "Most likely a stale forwarder POSTed after the tab closed, or a " +
                    "session id mismatch between the hook command and the tab launch env.");
                ctx.Response.StatusCode = 204;
                ctx.Response.Close();
                return;
            }

            HookEvent? evt = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                // Claude Code's hook envelope keys the event name as
                // `hook_event_name` and the tool as `tool_name`. Older shape
                // (`type`/`tool`) is kept as a fallback so anything posting in
                // either form still routes — see hooks.jsonl samples. Codex
                // uses the same snake_case names plus turn_id / tool_use_id /
                // transcript_path / source / last_assistant_message.
                string ReadStr(params string[] names)
                {
                    foreach (var n in names)
                    {
                        if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                            return v.GetString() ?? "";
                    }
                    return "";
                }
                string? ReadRawObject(string name)
                {
                    if (root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object)
                        return v.GetRawText();
                    return null;
                }
                var typeName = ReadStr("hook_event_name", "type");
                var toolName = ReadStr("tool_name", "tool");
                var phaseName = ReadStr("phase");
                var message = ReadStr("message");
                var turnId = ReadStr("turn_id");
                var toolUseId = ReadStr("tool_use_id");
                var agentId = ReadStr("agent_id");
                var agentType = ReadStr("agent_type", "subagent_type");
                var transcriptPath = ReadStr("transcript_path");
                var sessionStartSource = ReadStr("source");
                var lastAssistantMessage = ReadStr("last_assistant_message");
                var model = ReadStr("model");
                var toolInput = ReadRawObject("tool_input");
                var toolResponse = ReadRawObject("tool_response");
                evt = new HookEvent(
                    sessionId,
                    typeName,
                    string.IsNullOrEmpty(toolName) ? null : toolName,
                    string.IsNullOrEmpty(phaseName) ? null : phaseName,
                    string.IsNullOrEmpty(message) ? null : message,
                    body)
                {
                    Kind = agentKind,
                    TurnId = string.IsNullOrEmpty(turnId) ? null : turnId,
                    ToolUseId = string.IsNullOrEmpty(toolUseId) ? null : toolUseId,
                    AgentId = string.IsNullOrEmpty(agentId) ? null : agentId,
                    AgentType = string.IsNullOrEmpty(agentType) ? null : agentType,
                    ToolInput = toolInput,
                    ToolResponse = toolResponse,
                    TranscriptPath = string.IsNullOrEmpty(transcriptPath) ? null : transcriptPath,
                    SessionStartSource = string.IsNullOrEmpty(sessionStartSource) ? null : sessionStartSource,
                    LastAssistantMessage = string.IsNullOrEmpty(lastAssistantMessage) ? null : lastAssistantMessage,
                    Model = string.IsNullOrEmpty(model) ? null : model,
                };
            }
            catch
            {
                evt = new HookEvent(sessionId, "raw", null, null, null, body) { Kind = agentKind };
            }

            AppendLog(evt, ctx.Request.RemoteEndPoint?.ToString());

            if (_routes.TryGetValue(sessionId, out var handler))
            {
                try { handler(evt); } catch { /* per-tab handler must not kill the gateway */ }
            }

            ctx.Response.StatusCode = 204;
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Abort(); } catch { }
        }
    }

    private void AppendStatusLog(string sessionId, string body, string? remote)
    {
        if (!DebugLogging) return;
        // Statusline pushes ~every 10s per tab — separate log file with the same
        // 5MB rotation so it doesn't drown the hook event log. Body is the full
        // CC StatusJSON; useful when diagnosing what fields a particular CC
        // version is sending.
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTime.UtcNow.ToString("o"),
            remote,
            session = sessionId,
            routed = !string.IsNullOrEmpty(sessionId) && _statusRoutes.ContainsKey(sessionId),
            body,
        });

        try
        {
            lock (_statusLogLock)
            {
                var fi = new FileInfo(StatusLogPath);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                {
                    var rotated = StatusLogPath + ".1";
                    try { if (File.Exists(rotated)) File.Delete(rotated); } catch { }
                    try { File.Move(StatusLogPath, rotated); } catch { }
                }
                File.AppendAllText(StatusLogPath, line + Environment.NewLine);
            }
        }
        catch { /* logging must never break the receive path */ }
    }

    private void AppendLog(HookEvent evt, string? remote)
    {
        if (!DebugLogging) return;
        // One JSONL line per event so the file stays grep/jq-friendly. Keep the
        // record skinny but include enough to diagnose routing: timestamp, who
        // posted, the parsed envelope, and whether we had a registered handler.
        var line = JsonSerializer.Serialize(new
        {
            ts = DateTime.UtcNow.ToString("o"),
            remote,
            session = evt.SessionId,
            kind = evt.Kind.ToString(),
            type = evt.Type,
            tool = evt.Tool,
            phase = evt.Phase,
            turn_id = evt.TurnId,
            tool_use_id = evt.ToolUseId,
            agent_id = evt.AgentId,
            agent_type = evt.AgentType,
            transcript_path = evt.TranscriptPath,
            routed = !string.IsNullOrEmpty(evt.SessionId) && _routes.ContainsKey(evt.SessionId),
            body = evt.RawBody,
        });

        try
        {
            lock (_logLock)
            {
                // Cap at ~5 MB — when over, rotate to .1 (overwriting any prior .1).
                // Cheap rotation without bringing in a logging dep for what is mostly
                // a debug aid.
                var fi = new FileInfo(LogPath);
                if (fi.Exists && fi.Length > 5 * 1024 * 1024)
                {
                    var rotated = LogPath + ".1";
                    try { if (File.Exists(rotated)) File.Delete(rotated); } catch { }
                    try { File.Move(LogPath, rotated); } catch { }
                }
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch { /* logging must never break the receive path */ }
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        try { if (_listener.IsListening) _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        _routes.Clear();
        _routeKinds.Clear();
    }
}

/// <summary>
/// Parsed hook envelope routed by <see cref="HooksGateway"/>. Carries fields from
/// both Claude Code and Codex CLI payloads; agent-specific fields are nullable
/// and set only when present on the wire. <see cref="Kind"/> is stamped by the
/// gateway from the registering tab's <see cref="HookAgentKind"/>; downstream
/// consumers branch on it where Claude/Codex behaviour differs.
/// </summary>
public sealed record HookEvent(string SessionId, string Type, string? Tool, string? Phase, string? Message, string RawBody)
{
    /// <summary>Which agent produced this event. Stamped at dispatch from the route table.</summary>
    public HookAgentKind Kind { get; init; } = HookAgentKind.Claude;

    /// <summary>Codex turn id (<c>turn_id</c>). Null for Claude payloads.</summary>
    public string? TurnId { get; init; }

    /// <summary>Tool-use id (<c>tool_use_id</c>). Codex emits per tool event; Claude also carries it on tool events.</summary>
    public string? ToolUseId { get; init; }

    /// <summary>Subagent id (<c>agent_id</c>) on Claude <c>SubagentStart</c>/<c>SubagentStop</c>. Null elsewhere and on CC versions that predate it.</summary>
    public string? AgentId { get; init; }

    /// <summary>Subagent type (<c>agent_type</c>, e.g. "Explore") on Claude <c>SubagentStart</c>/<c>SubagentStop</c>. Null elsewhere.</summary>
    public string? AgentType { get; init; }

    /// <summary>Raw JSON of the <c>tool_input</c> object as it arrived on the wire. Null when the payload omitted it.</summary>
    public string? ToolInput { get; init; }

    /// <summary>Raw JSON of the <c>tool_response</c> object as it arrived on the wire. Null when the payload omitted it.</summary>
    public string? ToolResponse { get; init; }

    /// <summary>Filesystem path to the agent's transcript JSONL (<c>transcript_path</c>). Stored verbatim; not consumed yet (Phase 2).</summary>
    public string? TranscriptPath { get; init; }

    /// <summary>Codex <c>SessionStart</c> source: <c>"startup" | "resume" | "clear"</c>. Null elsewhere.</summary>
    public string? SessionStartSource { get; init; }

    /// <summary>Codex <c>Stop</c> trailing assistant message (<c>last_assistant_message</c>). Null elsewhere.</summary>
    public string? LastAssistantMessage { get; init; }

    /// <summary>Active model identifier (<c>model</c>). Codex emits this on every event; Claude omits it.</summary>
    public string? Model { get; init; }
}
