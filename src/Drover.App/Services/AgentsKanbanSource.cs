using Drover.App.Models;
using Drover.App.ViewModels;

namespace Drover.App.Services;

/// <summary>
/// Read-only source surfacing discovered subagents. KanbanPanel renders this source
/// as a single rail (no tri-column split) — agents have no status. Drag is disabled.
/// </summary>
public sealed class AgentsKanbanSource : IKanbanSource
{
    public event System.EventHandler? CardsChanged;

    private readonly AgentCatalog _catalog;
    private readonly System.Collections.Generic.List<KanbanCard> _cards = new();
    private readonly object _lock = new();
    private TerminalTabViewModel? _tab;

    public AgentsKanbanSource(AgentCatalog catalog) => _catalog = catalog;

    public System.Collections.Generic.IReadOnlyList<KanbanCard> Cards
    {
        get { lock (_lock) return _cards.ToArray(); }
    }

    public bool SupportsDrag => false;

    public void Activate(TerminalTabViewModel? tab)
    {
        _tab = tab;
        _catalog.Changed += OnCatalogChanged;
        Reload();
    }

    public void Deactivate()
    {
        _catalog.Changed -= OnCatalogChanged;
        _tab = null;
        ReplaceCards(System.Array.Empty<KanbanCard>());
    }

    public System.Threading.Tasks.Task<bool> MoveCardAsync(KanbanCard card, KanbanStatus to)
        => System.Threading.Tasks.Task.FromResult(false);

    private void OnCatalogChanged(object? sender, System.EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) { Reload(); return; }
        dispatcher.BeginInvoke(new System.Action(Reload));
    }

    private void Reload()
    {
        var path = _tab?.Project.Path;
        var agents = _catalog.ForProject(path);
        var built = new System.Collections.Generic.List<KanbanCard>(agents.Count);
        foreach (var a in agents)
        {
            built.Add(new KanbanCard(
                Id: $"agent:{a.Name}",
                Status: KanbanStatus.Pending,
                Title: a.Name,
                Body: a.Description,
                Gates: System.Array.Empty<KanbanGate>(),
                SubChecklist: System.Array.Empty<KanbanSubItem>(),
                SourceRef: new AgentRef(a.Name),
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
