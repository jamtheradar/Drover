using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Text.Json;

namespace Drover.App.Services;

/// <summary>
/// Loopback HTTP listener that receives Claude Code hook events. Each tab launches
/// with a unique session UUID + this listener's URL injected as env vars; Claude's
/// configured hooks POST tool/permission/lifecycle events back to us tagged with
/// the session id, which we route to the matching tab.
///
/// v0.2 scaffold: today this just receives and dispatches events. AttentionMonitor
/// (OSC scraping) remains the primary signal in v0.1; this listener exists so the
/// piping is in place when we wire user hook scripts in v0.2.
/// </summary>
public sealed class HooksGateway : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly ConcurrentDictionary<string, Action<HookEvent>> _routes = new();
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

    public void Register(string sessionId, Action<HookEvent> handler)
        => _routes[sessionId] = handler;

    public void Unregister(string sessionId) => _routes.TryRemove(sessionId, out _);

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

            HookEvent? evt = null;
            try
            {
                using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "{}" : body);
                var root = doc.RootElement;
                // Claude Code's hook envelope keys the event name as
                // `hook_event_name` and the tool as `tool_name`. Older shape
                // (`type`/`tool`) is kept as a fallback so anything posting in
                // either form still routes — see hooks.jsonl samples.
                string ReadStr(params string[] names)
                {
                    foreach (var n in names)
                    {
                        if (root.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String)
                            return v.GetString() ?? "";
                    }
                    return "";
                }
                var typeName = ReadStr("hook_event_name", "type");
                var toolName = ReadStr("tool_name", "tool");
                var phaseName = ReadStr("phase");
                var message = ReadStr("message");
                evt = new HookEvent(
                    sessionId,
                    typeName,
                    string.IsNullOrEmpty(toolName) ? null : toolName,
                    string.IsNullOrEmpty(phaseName) ? null : phaseName,
                    string.IsNullOrEmpty(message) ? null : message,
                    body);
            }
            catch
            {
                evt = new HookEvent(sessionId, "raw", null, null, null, body);
            }

            AppendLog(evt, ctx.Request.RemoteEndPoint?.ToString());

            if (!string.IsNullOrEmpty(sessionId) && _routes.TryGetValue(sessionId, out var handler))
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
            type = evt.Type,
            tool = evt.Tool,
            phase = evt.Phase,
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
    }
}

public sealed record HookEvent(string SessionId, string Type, string? Tool, string? Phase, string? Message, string RawBody);
