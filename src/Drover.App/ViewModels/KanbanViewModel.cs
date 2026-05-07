using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Drover.App.Models;
using Drover.App.Services;

namespace Drover.App.ViewModels;

/// <summary>
/// Hosts the kanban board: an active <see cref="IKanbanSource"/> and three observable
/// columns. ActiveSource changes tear down the prior source and rebuild from the new
/// one. Tab changes propagate to the source via <see cref="OnTabChanged"/>.
///
/// All source events fan out through <see cref="System.Windows.Application.Current.Dispatcher"/>
/// — sources may raise from background threads (file watchers, hook gateway).
/// </summary>
public sealed partial class KanbanViewModel : ObservableObject
{
    private readonly LocalFilesKanbanSource _localSource;
    private readonly LiveTodosKanbanSource _liveSource;
    private readonly AgentsKanbanSource _agentsSource;
    private readonly AgentCatalog _agents;
    private readonly GateRegistry _gates;
    private readonly ToastNotifier _toaster;
    private readonly SettingsStore? _settings;
    private TerminalTabViewModel? _tab;
    private bool _restoringSource;

    [ObservableProperty] private KanbanSourceKind _activeSource = KanbanSourceKind.Local;
    [ObservableProperty] private IKanbanSource? _currentSource;
    [ObservableProperty] private bool _supportsDrag;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _agentsRailVisible;

    public ObservableCollection<KanbanCard> Pending { get; } = new();
    public ObservableCollection<KanbanCard> Planned { get; } = new();
    public ObservableCollection<KanbanCard> InProgress { get; } = new();
    public ObservableCollection<KanbanCard> Completed { get; } = new();
    public ObservableCollection<AgentDefinition> AvailableAgents { get; } = new();

    public KanbanViewModel(LiveTodoStore liveTodos, AgentCatalog agents, GateRegistry gates, ToastNotifier toaster, SettingsStore? settings = null)
    {
        _agents = agents;
        _gates = gates;
        _toaster = toaster;
        _settings = settings;
        _localSource = new LocalFilesKanbanSource(gates);
        _liveSource = new LiveTodosKanbanSource(liveTodos);
        _agentsSource = new AgentsKanbanSource(agents);

        SwitchSourceCommand = new RelayCommand<KanbanSourceKind>(k => ActiveSource = k);
        MoveCardCommand = new AsyncRelayCommand<MoveCardArg>(MoveCardAsync);
        RefreshCommand = new RelayCommand(Refresh);
        DispatchToAgentCommand = new RelayCommand<DispatchArg>(DispatchToAgent);

        // Restore last-used source if persisted. Suppress save during restore so the
        // initial OnActiveSourceChanged doesn't write a redundant settings update. If
        // the restored value differs from the default (Local), the partial setter will
        // wire the source. If it's the same as the default, we have to initialize
        // manually because change-notifications don't fire for unchanged values.
        var restoredKind = KanbanSourceKind.Local;
        if (_settings is not null)
        {
            var raw = _settings.Current.KanbanLastSource;
            if (!string.IsNullOrEmpty(raw) && System.Enum.TryParse<KanbanSourceKind>(raw, ignoreCase: true, out var k))
                restoredKind = k;
        }
        _restoringSource = true;
        if (restoredKind != ActiveSource)
        {
            ActiveSource = restoredKind;
        }
        else
        {
            InitializeSource(restoredKind);
        }
        _restoringSource = false;

        _agents.Changed += (_, _) =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null) RebuildAgents();
            else dispatcher.BeginInvoke(new System.Action(RebuildAgents));
        };
    }

    private void PersistSource()
    {
        if (_settings is null || _restoringSource) return;
        var s = _settings.Current;
        if (string.Equals(s.KanbanLastSource, ActiveSource.ToString(), System.StringComparison.Ordinal)) return;
        _settings.Update(s with { KanbanLastSource = ActiveSource.ToString() });
    }

    private void Refresh()
    {
        switch (CurrentSource)
        {
            case LocalFilesKanbanSource l: l.Reload(); break;
            // Live and Agents have no manual reload — they react to events.
            default: RebuildColumns(); break;
        }
    }

    public RelayCommand<KanbanSourceKind> SwitchSourceCommand { get; }
    public AsyncRelayCommand<MoveCardArg> MoveCardCommand { get; }
    public RelayCommand RefreshCommand { get; }
    public RelayCommand<DispatchArg> DispatchToAgentCommand { get; }

    /// <summary>Called by ShellViewModel when the active terminal tab changes.</summary>
    public void OnTabChanged(TerminalTabViewModel? tab)
    {
        _tab = tab;
        // Re-activate the current source against the new tab so file watchers rebind
        // and Live source re-targets the new session.
        if (CurrentSource is not null)
        {
            CurrentSource.Deactivate();
            CurrentSource.Activate(tab);
        }
        RebuildAgents();
    }

    private void RebuildAgents()
    {
        AvailableAgents.Clear();
        var path = _tab?.Project.Path;
        foreach (var a in _agents.ForProject(path)) AvailableAgents.Add(a);
    }

    private void DispatchToAgent(DispatchArg? arg)
    {
        if (arg is null || _tab is null) return;
        // Format consistent with Claude Code's recommended subagent invocation:
        // `Use the @<name> subagent to: <task>`. SendInput with appendReturn=true
        // commits the prompt automatically.
        var title = string.IsNullOrWhiteSpace(arg.Card.Title) ? "<no title>" : arg.Card.Title;
        var prompt = $"Use the @{arg.Agent.Name} subagent to: {title}";
        try { _tab.SendInput(prompt); } catch { /* terminal teardown — silently drop */ }
    }

    partial void OnActiveSourceChanged(KanbanSourceKind value)
    {
        InitializeSource(value);
        PersistSource();
    }

    private void InitializeSource(KanbanSourceKind value)
    {
        if (CurrentSource is not null)
        {
            CurrentSource.CardsChanged -= OnCardsChanged;
            CurrentSource.Deactivate();
        }

        IKanbanSource next = value switch
        {
            KanbanSourceKind.Local => _localSource,
            KanbanSourceKind.Live => _liveSource,
            KanbanSourceKind.Agents => _agentsSource,
            _ => _localSource,
        };
        CurrentSource = next;
        SupportsDrag = next.SupportsDrag;
        AgentsRailVisible = value == KanbanSourceKind.Agents;
        next.CardsChanged += OnCardsChanged;
        next.Activate(_tab);
        RebuildColumns();
    }

    private void OnCardsChanged(object? sender, System.EventArgs e)
    {
        AppLog.Info("Kanban.VM", $"OnCardsChanged fired from source={(sender?.GetType().Name ?? "null")}.");
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) RebuildColumns();
        else dispatcher.BeginInvoke(new System.Action(RebuildColumns));
    }

    public void RebuildColumns()
    {
        var src = CurrentSource;
        Pending.Clear();
        Planned.Clear();
        InProgress.Clear();
        Completed.Clear();
        if (src is null) { AppLog.Warn("Kanban.VM", "RebuildColumns: CurrentSource is null."); StatusText = "no source"; return; }

        var srcCards = src.Cards;
        AppLog.Info("Kanban.VM", $"RebuildColumns: src={src.GetType().Name}, src.Cards.Count={srcCards.Count}.");
        foreach (var c in srcCards)
        {
            switch (c.Status)
            {
                case KanbanStatus.Pending: Pending.Add(c); break;
                case KanbanStatus.Planned: Planned.Add(c); break;
                case KanbanStatus.InProgress: InProgress.Add(c); break;
                case KanbanStatus.Completed: Completed.Add(c); break;
            }
        }
        AppLog.Info("Kanban.VM", $"RebuildColumns done: backlog={Pending.Count}, planned={Planned.Count}, inProgress={InProgress.Count}, completed={Completed.Count}.");
        StatusText = $"{Pending.Count} backlog · {Planned.Count} planned · {InProgress.Count} in progress · {Completed.Count} done";
    }

    private async System.Threading.Tasks.Task MoveCardAsync(MoveCardArg? arg)
    {
        if (arg is null || CurrentSource is null) return;
        if (arg.Card.Status == arg.Target) return;

        // Gate check: only enforced when moving INTO Completed. Pending↔InProgress
        // are free-flowing — gates exist to keep "done" honest.
        if (arg.Target == KanbanStatus.Completed && arg.Card.Gates.Count > 0)
        {
            var blockers = new System.Collections.Generic.List<string>();
            foreach (var g in arg.Card.Gates)
                if (!g.Passed) blockers.Add($"{g.Kind}: {g.Detail ?? "failed"}");
            if (blockers.Count > 0)
            {
                _toaster.NotifyKanbanGateBlocked(arg.Card.Title, string.Join("; ", blockers));
                return;
            }
        }

        var ok = await CurrentSource.MoveCardAsync(arg.Card, arg.Target).ConfigureAwait(true);
        if (!ok) return;

        // Optimistic in-memory move so the UI is responsive — the source's eventual
        // Reload (post-debounce) will reconcile.
        var src = arg.Card.SourceRef;
        var moved = arg.Card with { Status = arg.Target };
        RemoveById(arg.Card.Id);
        switch (arg.Target)
        {
            case KanbanStatus.Pending: Pending.Add(moved); break;
            case KanbanStatus.Planned: Planned.Add(moved); break;
            case KanbanStatus.InProgress: InProgress.Add(moved); break;
            case KanbanStatus.Completed: Completed.Add(moved); break;
        }
        StatusText = $"{Pending.Count} backlog · {Planned.Count} planned · {InProgress.Count} in progress · {Completed.Count} done";
    }

    private void RemoveById(string id)
    {
        for (int i = Pending.Count - 1; i >= 0; i--) if (Pending[i].Id == id) Pending.RemoveAt(i);
        for (int i = Planned.Count - 1; i >= 0; i--) if (Planned[i].Id == id) Planned.RemoveAt(i);
        for (int i = InProgress.Count - 1; i >= 0; i--) if (InProgress[i].Id == id) InProgress.RemoveAt(i);
        for (int i = Completed.Count - 1; i >= 0; i--) if (Completed[i].Id == id) Completed.RemoveAt(i);
    }
}

public sealed record MoveCardArg(KanbanCard Card, KanbanStatus Target);
public sealed record DispatchArg(KanbanCard Card, AgentDefinition Agent);
