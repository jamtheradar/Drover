using System.Collections.Concurrent;
using Drover.App.Models;

namespace Drover.App.Services;

/// <summary>
/// Holds the latest todo/task list per Claude Code session. ShellViewModel/
/// SessionStore wrap their per-session HooksGateway handler to also call
/// <see cref="Ingest(HookEvent)"/>, which dispatches by tool name:
///
/// <list type="bullet">
///   <item><c>TodoWrite</c> (legacy): full list snapshot in <c>tool_input.todos[]</c>.</item>
///   <item><c>TaskCreate</c>: append a single entry from <c>tool_input</c> +
///     <c>tool_response.task.id</c>.</item>
///   <item><c>TaskUpdate</c>: overlay status/subject/description on the entry whose
///     id matches <c>tool_input.taskId</c>.</item>
///   <item><c>TaskList</c>: authoritative snapshot from <c>tool_response.tasks[]</c>;
///     replaces the per-session list wholesale.</item>
/// </list>
///
/// <see cref="Updated"/> fires on every replacement; consumers (LiveTodosKanbanSource)
/// marshal back to UI before touching observable state.
/// </summary>
public sealed class LiveTodoStore
{
    private readonly ConcurrentDictionary<string, System.Collections.Generic.IReadOnlyList<TodoEntry>> _bySession = new();
    // Coarse lock guards Create/Update mutations so a fast Create+Update sequence
    // on the gateway listener thread can't lose an event. Volume is low.
    private readonly object _mutationLock = new();

    public event System.EventHandler<string>? Updated;

    public System.Collections.Generic.IReadOnlyList<TodoEntry> ForSession(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return System.Array.Empty<TodoEntry>();
        return _bySession.TryGetValue(sessionId, out var list) ? list : System.Array.Empty<TodoEntry>();
    }

    public void Clear(string sessionId)
    {
        if (string.IsNullOrEmpty(sessionId)) return;
        if (_bySession.TryRemove(sessionId, out _))
            Updated?.Invoke(this, sessionId);
    }

    /// <summary>
    /// Inspects a hook event; if it's a <c>PostToolUse</c> for one of the supported
    /// todo/task tools, parses the payload and updates the per-session list.
    /// Other events are ignored.
    /// </summary>
    public void Ingest(HookEvent evt)
    {
        if (evt is null) return;
        if (string.IsNullOrEmpty(evt.SessionId)) { AppLog.Warn("LiveTodos", "Ingest dropped: empty SessionId."); return; }
        if (!string.Equals(evt.Type, "PostToolUse", System.StringComparison.OrdinalIgnoreCase)) { AppLog.Info("LiveTodos", $"Ingest skipped (type='{evt.Type}', not PostToolUse), session={evt.SessionId}, tool={evt.Tool}."); return; }
        if (string.IsNullOrWhiteSpace(evt.RawBody)) { AppLog.Warn("LiveTodos", $"Ingest dropped (empty RawBody) for session={evt.SessionId}."); return; }

        var tool = evt.Tool ?? string.Empty;
        bool handled = string.Equals(tool, "TodoWrite", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, "TaskCreate", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, "TaskUpdate", System.StringComparison.OrdinalIgnoreCase)
            || string.Equals(tool, "TaskList", System.StringComparison.OrdinalIgnoreCase);
        if (!handled) { AppLog.Info("LiveTodos", $"Ingest skipped (tool='{tool}', not a todo/task tool), session={evt.SessionId}."); return; }

        AppLog.Info("LiveTodos", $"Ingest: {tool} for session={evt.SessionId}, body length={evt.RawBody.Length}.");

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(evt.RawBody);
            var root = doc.RootElement;

            bool changed;
            if (string.Equals(tool, "TodoWrite", System.StringComparison.OrdinalIgnoreCase))
                changed = HandleTodoWrite(evt.SessionId, root);
            else if (string.Equals(tool, "TaskCreate", System.StringComparison.OrdinalIgnoreCase))
                changed = HandleTaskCreate(evt.SessionId, root);
            else if (string.Equals(tool, "TaskUpdate", System.StringComparison.OrdinalIgnoreCase))
                changed = HandleTaskUpdate(evt.SessionId, root);
            else
                changed = HandleTaskList(evt.SessionId, root);

            if (changed)
            {
                AppLog.Info("LiveTodos", $"Ingest stored {_bySession.GetValueOrDefault(evt.SessionId)?.Count ?? 0} todo(s) for session={evt.SessionId}; firing Updated.");
                Updated?.Invoke(this, evt.SessionId);
            }
        }
        catch (System.Exception ex)
        {
            // Malformed payloads are not fatal — leave the prior list intact.
            AppLog.Error("LiveTodos", $"Ingest parse failed for session={evt.SessionId}.", ex);
        }
    }

    private bool HandleTodoWrite(string sessionId, System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("tool_input", out var toolInput)) { AppLog.Warn("LiveTodos", $"TodoWrite: no 'tool_input' in body. Top-level keys: {string.Join(",", EnumerateKeys(root))}."); return false; }
        if (!toolInput.TryGetProperty("todos", out var todos)) { AppLog.Warn("LiveTodos", $"TodoWrite: no 'todos' in tool_input. Keys: {string.Join(",", EnumerateKeys(toolInput))}."); return false; }
        if (todos.ValueKind != System.Text.Json.JsonValueKind.Array) { AppLog.Warn("LiveTodos", $"TodoWrite: 'todos' is {todos.ValueKind}, expected Array."); return false; }

        var list = new System.Collections.Generic.List<TodoEntry>();
        int idx = 0;
        foreach (var t in todos.EnumerateArray())
        {
            string content = ReadString(t, "content");
            string status = ReadString(t, "status", fallback: "pending");
            string? activeForm = ReadOptionalString(t, "activeForm");
            if (string.IsNullOrEmpty(content)) { idx++; continue; }
            list.Add(new TodoEntry(idx.ToString(System.Globalization.CultureInfo.InvariantCulture), content, status, activeForm));
            idx++;
        }
        _bySession[sessionId] = list;
        return true;
    }

    private bool HandleTaskCreate(string sessionId, System.Text.Json.JsonElement root)
    {
        root.TryGetProperty("tool_input", out var toolInput);
        root.TryGetProperty("tool_response", out var toolResponse);

        string? id = null;
        if (toolResponse.ValueKind == System.Text.Json.JsonValueKind.Object
            && toolResponse.TryGetProperty("task", out var taskEl)
            && taskEl.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            id = ReadOptionalString(taskEl, "id");
        }
        if (string.IsNullOrEmpty(id)) { AppLog.Warn("LiveTodos", $"TaskCreate: no 'tool_response.task.id'. Top-level keys: {string.Join(",", EnumerateKeys(root))}."); return false; }

        string content = toolInput.ValueKind == System.Text.Json.JsonValueKind.Object
            ? ReadString(toolInput, "subject")
            : string.Empty;
        string? activeForm = toolInput.ValueKind == System.Text.Json.JsonValueKind.Object
            ? ReadOptionalString(toolInput, "description") ?? ReadOptionalString(toolInput, "activeForm")
            : null;

        lock (_mutationLock)
        {
            var current = _bySession.TryGetValue(sessionId, out var l) ? l : System.Array.Empty<TodoEntry>();
            // Replace if this id already exists (idempotent re-fire), else append.
            var next = new System.Collections.Generic.List<TodoEntry>(current.Count + 1);
            bool replaced = false;
            foreach (var e in current)
            {
                if (string.Equals(e.Id, id, System.StringComparison.Ordinal))
                {
                    next.Add(new TodoEntry(id!, content.Length > 0 ? content : e.Content, e.Status, activeForm ?? e.ActiveForm));
                    replaced = true;
                }
                else next.Add(e);
            }
            if (!replaced)
                next.Add(new TodoEntry(id!, content, "pending", activeForm));
            _bySession[sessionId] = next;
        }
        return true;
    }

    private bool HandleTaskUpdate(string sessionId, System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("tool_input", out var toolInput) || toolInput.ValueKind != System.Text.Json.JsonValueKind.Object)
        { AppLog.Warn("LiveTodos", $"TaskUpdate: missing tool_input. Top-level keys: {string.Join(",", EnumerateKeys(root))}."); return false; }
        string? id = ReadOptionalString(toolInput, "taskId");
        if (string.IsNullOrEmpty(id)) { AppLog.Warn("LiveTodos", $"TaskUpdate: no 'taskId' in tool_input. Keys: {string.Join(",", EnumerateKeys(toolInput))}."); return false; }

        string? newStatus = ReadOptionalString(toolInput, "status");
        string? newSubject = ReadOptionalString(toolInput, "subject");
        string? newDescription = ReadOptionalString(toolInput, "description");

        lock (_mutationLock)
        {
            var current = _bySession.TryGetValue(sessionId, out var l) ? l : System.Array.Empty<TodoEntry>();
            var next = new System.Collections.Generic.List<TodoEntry>(current.Count);
            bool found = false;
            foreach (var e in current)
            {
                if (string.Equals(e.Id, id, System.StringComparison.Ordinal))
                {
                    next.Add(new TodoEntry(
                        e.Id,
                        newSubject ?? e.Content,
                        newStatus ?? e.Status,
                        newDescription ?? e.ActiveForm));
                    found = true;
                }
                else next.Add(e);
            }
            if (!found)
            {
                // Create-on-update: keep state honest if we missed the create.
                next.Add(new TodoEntry(id!, newSubject ?? string.Empty, newStatus ?? "pending", newDescription));
                AppLog.Info("LiveTodos", $"TaskUpdate: id={id} not found in session={sessionId}; appended.");
            }
            _bySession[sessionId] = next;
        }
        return true;
    }

    private bool HandleTaskList(string sessionId, System.Text.Json.JsonElement root)
    {
        if (!root.TryGetProperty("tool_response", out var toolResponse) || toolResponse.ValueKind != System.Text.Json.JsonValueKind.Object)
        { AppLog.Warn("LiveTodos", $"TaskList: missing tool_response. Top-level keys: {string.Join(",", EnumerateKeys(root))}."); return false; }
        if (!toolResponse.TryGetProperty("tasks", out var tasks) || tasks.ValueKind != System.Text.Json.JsonValueKind.Array)
        { AppLog.Warn("LiveTodos", $"TaskList: no 'tasks' array in tool_response. Keys: {string.Join(",", EnumerateKeys(toolResponse))}."); return false; }

        var list = new System.Collections.Generic.List<TodoEntry>();
        foreach (var t in tasks.EnumerateArray())
        {
            if (t.ValueKind != System.Text.Json.JsonValueKind.Object) continue;
            string? id = ReadOptionalString(t, "id");
            if (string.IsNullOrEmpty(id)) continue;
            string content = ReadString(t, "subject");
            string status = ReadString(t, "status", fallback: "pending");
            string? activeForm = ReadOptionalString(t, "description") ?? ReadOptionalString(t, "activeForm");
            list.Add(new TodoEntry(id!, content, status, activeForm));
        }
        _bySession[sessionId] = list;
        return true;
    }

    private static string ReadString(System.Text.Json.JsonElement el, string name, string fallback = "")
    {
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return fallback;
        if (!el.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind == System.Text.Json.JsonValueKind.String ? (v.GetString() ?? fallback) : fallback;
    }

    private static string? ReadOptionalString(System.Text.Json.JsonElement el, string name)
    {
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
    }

    private static System.Collections.Generic.IEnumerable<string> EnumerateKeys(System.Text.Json.JsonElement el)
    {
        if (el.ValueKind != System.Text.Json.JsonValueKind.Object) yield break;
        foreach (var p in el.EnumerateObject()) yield return p.Name;
    }
}
