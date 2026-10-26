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

    /// <summary>
    /// Own instance rather than a shared/DI one: <see cref="SpecWatcher"/> is
    /// per-folder and cheap, and <see cref="ViewModels.SpecsPanelViewModel"/>
    /// already keeps its own. Armed in <see cref="Activate"/>, stopped in
    /// <see cref="Deactivate"/> so no handle is left on <c>specs/</c>.
    /// </summary>
    private readonly SpecWatcher _specWatcher = new();

    public AgentsKanbanSource(AgentCatalog catalog)
    {
        _catalog = catalog;
        // Subscribed once for the lifetime of the source; the watcher is armed
        // and disarmed per activation, so an idle source raises nothing.
        _specWatcher.Changed += OnSpecsChanged;
    }

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
        // Watch() no-ops when the project has no specs/ folder yet.
        if (tab is not null) _specWatcher.Watch(tab.Project.Path);
        Reload();
    }

    public void Deactivate()
    {
        _catalog.Changed -= OnCatalogChanged;
        if (_tab is not null) _tab.RunningAgentsChanged -= OnRunningAgentsChanged;
        _specWatcher.Stop();
        _tab = null;
        ReplaceCards(System.Array.Empty<KanbanCard>());
    }

    public System.Threading.Tasks.Task<bool> MoveCardAsync(KanbanCard card, KanbanStatus to)
        => System.Threading.Tasks.Task.FromResult(false);

    private void OnCatalogChanged(object? sender, System.EventArgs e) => ReloadOnUiThread();

    // Raised from the hooks gateway's background thread on Task launch/return.
    private void OnRunningAgentsChanged(object? sender, System.EventArgs e) => ReloadOnUiThread();

    // Raised from the watcher's debounce timer thread.
    private void OnSpecsChanged(object? sender, System.EventArgs e) => ReloadOnUiThread();

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
        // Assigned-spec workload, derived purely from specs/*.md frontmatter.
        var specLoad = BuildSpecLoad(path);
        var built = new System.Collections.Generic.List<KanbanCard>(agents.Count + running.Count + specLoad.Count);
        var catalogNames = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var a in agents)
        {
            catalogNames.Add(a.Name);
            built.Add(new KanbanCard(
                Id: $"agent:{a.Name}",
                Status: KanbanStatus.Pending,
                Title: a.Name,
                Body: a.Description,
                Gates: System.Array.Empty<KanbanGate>(),
                SubChecklist: System.Array.Empty<KanbanSubItem>(),
                SourceRef: new AgentRef(a.Name),
                Badges: BadgesFor(a.Name, running.Contains(a.Name), specLoad)));
        }

        // Synthetic cards for names with no catalog file: running built-ins
        // (Explore, general-purpose, plugin agents) and names that only appear
        // in a spec's agent: field. Without the latter, assigning a built-in
        // makes its count invisible. Sorted so the rail stays stable.
        var extras = new System.Collections.Generic.List<string>();
        foreach (var name in running) if (!catalogNames.Contains(name)) extras.Add(name);
        foreach (var name in specLoad.Keys)
            if (!catalogNames.Contains(name) && !running.Contains(name)) extras.Add(name);
        extras.Sort(System.StringComparer.OrdinalIgnoreCase);

        foreach (var name in extras)
        {
            var assignedBySpecs = specLoad.ContainsKey(name);
            built.Add(new KanbanCard(
                Id: $"agent:{name}",
                Status: KanbanStatus.Pending,
                Title: name,
                Body: assignedBySpecs
                    ? "Assigned by specs, no local definition"
                    : "Built-in agent (no local definition)",
                Gates: System.Array.Empty<KanbanGate>(),
                SubChecklist: System.Array.Empty<KanbanSubItem>(),
                SourceRef: new AgentRef(name),
                Badges: BadgesFor(name, running.Contains(name), specLoad)));
        }
        ReplaceCards(built);
    }

    private static KanbanBadges BadgesFor(
        string agentName,
        bool pulse,
        System.Collections.Generic.IReadOnlyDictionary<string, SpecLoad> specLoad)
    {
        if (!specLoad.TryGetValue(agentName, out var load))
            return new KanbanBadges(ActivePulse: pulse);

        return new KanbanBadges(
            ActivePulse: pulse,
            AssignedSpecs: load.Ids.Count,
            DoneSpecs: load.Done,
            // Frontmatter-derived, not hook-derived: an in-progress spec with a
            // dead session still shows here, and that's the point.
            RunningSpecLabel: load.InProgressId is null ? null : $"\u25B6 {load.InProgressId}",
            AssignedSpecIds: load.Ids);
    }

    /// <summary>
    /// Reads <c>specs/*.md</c> and buckets them by their <c>agent:</c> field
    /// (case-insensitive; specs with an empty field are skipped). Follows the
    /// MemoryScanner / GitStatusService shape: derive from disk, refresh on a
    /// debounced watcher, hold nothing between reloads.
    /// </summary>
    private static System.Collections.Generic.Dictionary<string, SpecLoad> BuildSpecLoad(string? projectPath)
    {
        var map = new System.Collections.Generic.Dictionary<string, SpecLoad>(System.StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrEmpty(projectPath)) return map;
        var dir = System.IO.Path.Combine(projectPath!, "specs");
        if (!System.IO.Directory.Exists(dir)) return map;

        System.Collections.Generic.IReadOnlyList<SpecFile> specs;
        try { specs = SpecFile.Discover(dir); }
        catch { return map; }

        foreach (var spec in specs)
        {
            if (string.IsNullOrWhiteSpace(spec.Agent)) continue;
            var name = spec.Agent.Trim();
            if (!map.TryGetValue(name, out var load))
            {
                load = new SpecLoad();
                map[name] = load;
            }
            load.Ids.Add(spec.Id);
            if (spec.Status == SpecStatus.Done) load.Done++;
            // First in-progress spec in discovery (id-ascending) order wins.
            if (load.InProgressId is null && spec.Status == SpecStatus.InProgress)
                load.InProgressId = spec.Id;
        }
        return map;
    }

    /// <summary>Per-agent tally built by <see cref="BuildSpecLoad"/>. Mutable and
    /// short-lived — it exists only for the duration of one <see cref="Reload"/>.</summary>
    private sealed class SpecLoad
    {
        public System.Collections.Generic.List<string> Ids { get; } = new();
        public int Done;
        public string? InProgressId;
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
