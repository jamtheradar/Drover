using Drover.App.Models;
using Drover.App.ViewModels;

namespace Drover.App.Services;

/// <summary>
/// Read-only source projecting the active terminal tab's most recent <c>TodoWrite</c>
/// payload into kanban cards. Status maps directly from the raw status string. Drag
/// is disabled — Claude owns its own todo list.
/// </summary>
public sealed class LiveTodosKanbanSource : IKanbanSource
{
    public event System.EventHandler? CardsChanged;

    private readonly LiveTodoStore _store;
    private readonly System.Collections.Generic.List<KanbanCard> _cards = new();
    private readonly object _lock = new();
    private TerminalTabViewModel? _tab;

    public LiveTodosKanbanSource(LiveTodoStore store) => _store = store;

    public System.Collections.Generic.IReadOnlyList<KanbanCard> Cards
    {
        get { lock (_lock) return _cards.ToArray(); }
    }

    public bool SupportsDrag => false;

    public void Activate(TerminalTabViewModel? tab)
    {
        _tab = tab;
        _store.Updated += OnStoreUpdated;
        AppLog.Info("Kanban.Live", $"Activate: tab={(tab?.Project.Name ?? "null")}, sessionId={(tab?.SessionId ?? "null")}.");
        Reload();
    }

    public void Deactivate()
    {
        _store.Updated -= OnStoreUpdated;
        _tab = null;
        ReplaceCards(System.Array.Empty<KanbanCard>());
    }

    public System.Threading.Tasks.Task<bool> MoveCardAsync(KanbanCard card, KanbanStatus to)
        => System.Threading.Tasks.Task.FromResult(false);

    private void OnStoreUpdated(object? sender, string sessionId)
    {
        if (_tab is null) { AppLog.Info("Kanban.Live", $"OnStoreUpdated session={sessionId} ignored — _tab is null."); return; }
        if (!string.Equals(sessionId, _tab.SessionId, System.StringComparison.Ordinal))
        {
            AppLog.Info("Kanban.Live", $"OnStoreUpdated session={sessionId} ignored — does not match active tab session={_tab.SessionId}.");
            return;
        }
        AppLog.Info("Kanban.Live", $"OnStoreUpdated session={sessionId} matches active tab — reloading.");
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) { Reload(); return; }
        dispatcher.BeginInvoke(new System.Action(Reload));
    }

    private void Reload()
    {
        var tab = _tab;
        if (tab is null) { AppLog.Info("Kanban.Live", "Reload skipped — _tab is null."); ReplaceCards(System.Array.Empty<KanbanCard>()); return; }

        var todos = _store.ForSession(tab.SessionId);
        AppLog.Info("Kanban.Live", $"Reload: store has {todos.Count} todo(s) for session={tab.SessionId}.");
        var built = new System.Collections.Generic.List<KanbanCard>(todos.Count);
        foreach (var t in todos)
        {
            var status = t.Status switch
            {
                "in_progress" => KanbanStatus.InProgress,
                "completed"   => KanbanStatus.Completed,
                _             => KanbanStatus.Pending,
            };
            built.Add(new KanbanCard(
                Id: $"{tab.SessionId}#{t.Id}",
                Status: status,
                Title: t.Content,
                Body: t.ActiveForm,
                Gates: System.Array.Empty<KanbanGate>(),
                SubChecklist: System.Array.Empty<KanbanSubItem>(),
                SourceRef: new LiveTodoRef(tab.SessionId, t.Id),
                Badges: new KanbanBadges()));
        }
        ReplaceCards(built);
    }

    private void ReplaceCards(System.Collections.Generic.IReadOnlyList<KanbanCard> next)
    {
        lock (_lock)
        {
            _cards.Clear();
            _cards.AddRange(next);
        }
        CardsChanged?.Invoke(this, System.EventArgs.Empty);
    }
}
