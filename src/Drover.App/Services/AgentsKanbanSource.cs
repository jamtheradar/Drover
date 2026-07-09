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
        if (tab is not null) tab.RunningAgentsChanged += OnRunningAgentsChanged;
        Reload();
    }

    public void Deactivate()
    {
        _catalog.Changed -= OnCatalogChanged;
        if (_tab is not null) _tab.RunningAgentsChanged -= OnRunningAgentsChanged;
        _tab = null;
        ReplaceCards(System.Array.Empty<KanbanCard>());
    }

    public System.Threading.Tasks.Task<bool> MoveCardAsync(KanbanCard card, KanbanStatus to)
        => System.Threading.Tasks.Task.FromResult(false);

    private void OnCatalogChanged(object? sender, System.EventArgs e) => ReloadOnUiThread();

    // Raised from the hooks gateway's background thread on Task launch/return.
    private void OnRunningAgentsChanged(object? sender, System.EventArgs e) => ReloadOnUiThread();

    private void ReloadOnUiThread()
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) { Reload(); return; }
        dispatcher.BeginInvoke(new System.Action(Reload));
    }

    private void Reload()
    {
        var tab = _tab;
        var path = tab?.Project.Path;
        var agents = _catalog.ForProject(path);
        // Working set from the tab's hook stream — pulse the matching catalog
        // card; Remove() doubles as contains-and-consume so leftovers are the
        // running types with no catalog file (built-ins like general-purpose,
        // Explore, plugin agents), which get a synthetic card below.
        var running = new System.Collections.Generic.HashSet<string>(
            tab?.RunningAgentTypes ?? (System.Collections.Generic.IReadOnlyList<string>)System.Array.Empty<string>(),
            System.StringComparer.OrdinalIgnoreCase);
        var built = new System.Collections.Generic.List<KanbanCard>(agents.Count + running.Count);
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
                Badges: new KanbanBadges(ActivePulse: running.Remove(a.Name))));
        }
        foreach (var name in running)
        {
            built.Add(new KanbanCard(
                Id: $"agent:{name}",
                Status: KanbanStatus.Pending,
                Title: name,
                Body: "Built-in agent (no local definition)",
                Gates: System.Array.Empty<KanbanGate>(),
                SubChecklist: System.Array.Empty<KanbanSubItem>(),
                SourceRef: new AgentRef(name),
                Badges: new KanbanBadges(ActivePulse: true)));
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
