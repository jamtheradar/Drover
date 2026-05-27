using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Drover.App.Models;
using Drover.App.Services;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Drover.App.ViewModels;

/// <summary>
/// One row in the SpecsPanel list. <see cref="IsSelected"/> drives the
/// multi-select used by Run loop. The remaining fields are projections of
/// the underlying <see cref="SpecFile"/> so the row can be data-bound.
/// </summary>
public sealed partial class SpecRow : ObservableObject
{
    public SpecFile File { get; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string _statusGroup;
    [ObservableProperty] private string _statusBadge;
    [ObservableProperty] private string _severityBadge;
    [ObservableProperty] private string _lastRunText;

    public string Id => File.Id;
    public string Title => File.Title;
    public string FileName => File.FileName;
    public bool HasSeverity => SeverityBadge.Length > 0;

    public SpecRow(SpecFile file)
    {
        File = file;
        _statusGroup = StatusGroupFor(file.Status);
        _statusBadge = file.RawStatus.Length > 0 ? file.RawStatus : "—";
        _severityBadge = file.BlockSeverity switch
        {
            BlockSeverity.Trivial => "trivial",
            BlockSeverity.Major   => "major",
            _                     => string.Empty,
        };
        _lastRunText = file.LastRunAt;
    }

    private static string StatusGroupFor(SpecStatus s) => s switch
    {
        SpecStatus.InProgress => "1 · in-progress",
        SpecStatus.Pending    => "2 · pending",
        SpecStatus.Blocked    => "3 · blocked",
        SpecStatus.Done       => "4 · done",
        _                     => "5 · other",
    };
}

public sealed partial class SpecsPanelViewModel : ObservableObject
{
    private readonly IPromptLibrary _prompts;
    private readonly ILoopConfigService _loopConfig;
    private readonly ITemplateService _templateService;
    private readonly SpecWatcher _watcher;
    private readonly Dispatcher _dispatcher;

    /// <summary>Active tab the panel is bound to. Drives both project root
    /// resolution and the terminal session for loops. Updated by the shell.</summary>
    private TerminalTabViewModel? _tab;

    [ObservableProperty] private string _projectName = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;
    [ObservableProperty] private bool _hasSpecs;
    [ObservableProperty] private bool _hasProject;
    [ObservableProperty] private LoopMode _defaultMode = LoopMode.Drover;
    [ObservableProperty] private string _runLoopButtonLabel = "Run loop (Drover)";

    /// <summary>True when the active tab is a Codex project. Drives the
    /// session-driven menu-item disable + tooltip, and the primary
    /// Run-loop button disable when the project's default loop mode is
    /// session-driven (carry-over from a Claude-era config).</summary>
    [ObservableProperty] private bool _isCodexTab;
    [ObservableProperty] private bool _isSessionModeDisabled;
    [ObservableProperty] private string _sessionModeDisabledTooltip = string.Empty;

    /// <summary>
    /// Codex tab whose stored <c>default_loop_mode</c> is session-driven.
    /// The primary Run-loop button is disabled in that combination — silently
    /// falling back to Drover-driven would confuse the user about which mode
    /// actually ran. The default-mode setter only writes the config the user
    /// asked for; we don't auto-rewrite it on tab-load.
    /// </summary>
    [ObservableProperty] private bool _isPrimaryRunLoopBlocked;
    [ObservableProperty] private string _primaryRunLoopTooltip = "Run loop using the project default mode";

    /// <summary>Live progress chrome — set by <see cref="OnLoopEvent"/> as
    /// the runner reports back. Empty when no loop is active.</summary>
    [ObservableProperty] private string _progressText = string.Empty;
    [ObservableProperty] private bool _isLoopRunning;

    public ObservableCollection<SpecRow> Specs { get; } = new();

    public IRelayCommand RunLoopDefaultCommand { get; }
    public IRelayCommand RunLoopDroverCommand { get; }
    public IRelayCommand RunLoopSessionCommand { get; }
    public IRelayCommand SetDefaultDroverCommand { get; }
    public IRelayCommand SetDefaultSessionCommand { get; }
    public IRelayCommand StopLoopCommand { get; }
    public IRelayCommand RefreshCommand { get; }

    private CancellationTokenSource? _cts;

    public SpecsPanelViewModel(
        IPromptLibrary prompts,
        ILoopConfigService loopConfig,
        ITemplateService templateService)
    {
        _prompts = prompts;
        _loopConfig = loopConfig;
        _templateService = templateService;
        _dispatcher = System.Windows.Application.Current?.Dispatcher
                      ?? Dispatcher.CurrentDispatcher;
        _watcher = new SpecWatcher();
        _watcher.Changed += (_, _) => _dispatcher.BeginInvoke(new System.Action(Reload));

        RunLoopDefaultCommand = new RelayCommand(() => StartLoop(DefaultMode), () => CanRunLoop(DefaultMode));
        RunLoopDroverCommand = new RelayCommand(() => StartLoop(LoopMode.Drover), () => CanRunLoop(LoopMode.Drover));
        RunLoopSessionCommand = new RelayCommand(() => StartLoop(LoopMode.Session), () => CanRunLoop(LoopMode.Session));
        SetDefaultDroverCommand = new RelayCommand(() => SetDefault(LoopMode.Drover));
        SetDefaultSessionCommand = new RelayCommand(() => SetDefault(LoopMode.Session));
        StopLoopCommand = new RelayCommand(StopLoop, () => IsLoopRunning);
        RefreshCommand = new RelayCommand(Reload);
    }

    public void OnTabChanged(TerminalTabViewModel? tab)
    {
        _tab = tab;
        if (tab is null)
        {
            HasProject = false;
            ProjectName = string.Empty;
            Specs.Clear();
            HasSpecs = false;
            IsCodexTab = false;
            IsSessionModeDisabled = false;
            SessionModeDisabledTooltip = string.Empty;
            IsPrimaryRunLoopBlocked = false;
            PrimaryRunLoopTooltip = "Run loop using the project default mode";
            _watcher.Stop();
            UpdateButtonLabel();
            RaiseCommandStates();
            return;
        }

        HasProject = true;
        ProjectName = tab.Project.Name ?? string.Empty;
        DefaultMode = _loopConfig.GetDefaultMode(tab.Project.Path);
        IsCodexTab = tab.Project.Kind == ProjectKind.Codex;
        var isNonClaudeCli = IsCodexTab || tab.Project.Kind == ProjectKind.Gemini;
        IsSessionModeDisabled = isNonClaudeCli;
        SessionModeDisabledTooltip = isNonClaudeCli
            ? "Session-driven loop is Claude Code only."
            : string.Empty;
        UpdatePrimaryRunLoopState();
        UpdateButtonLabel();
        _watcher.Watch(tab.Project.Path);
        Reload();
        RaiseCommandStates();
    }

    private void UpdatePrimaryRunLoopState()
    {
        var blocked = IsCodexTab && DefaultMode == LoopMode.Session;
        IsPrimaryRunLoopBlocked = blocked;
        PrimaryRunLoopTooltip = blocked
            ? "Session-driven loop is Claude Code only."
            : "Run loop using the project default mode";
    }

    /// <summary>Re-scans the project's <c>specs/</c> folder. Called on tab
    /// change, panel-show, and watcher-debounce. Preserves selection across
    /// reloads so a long loop's user-interactions don't drop multi-select.</summary>
    private void Reload()
    {
        if (_tab is null) { Specs.Clear(); HasSpecs = false; return; }
        var dir = Path.Combine(_tab.Project.Path, "specs");
        if (!Directory.Exists(dir))
        {
            Specs.Clear();
            HasSpecs = false;
            StatusText = "No specs/ folder yet — author a spec to begin.";
            RaiseCommandStates();
            return;
        }

        var selectedIds = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var r in Specs) if (r.IsSelected) selectedIds.Add(r.FileName);

        var loaded = SpecFile.Discover(dir);
        Specs.Clear();
        foreach (var s in loaded)
        {
            var row = new SpecRow(s) { IsSelected = selectedIds.Contains(s.FileName) };
            Specs.Add(row);
        }
        HasSpecs = Specs.Count > 0;
        StatusText = HasSpecs ? string.Empty : "No specs found in specs/.";
        RaiseCommandStates();
    }

    private bool CanRunLoop(LoopMode mode)
    {
        if (_tab is null || IsLoopRunning) return false;
        // Session-driven mode is Claude-only — disable the command for Codex
        // tabs (drives the menu item's IsEnabled and the primary face button
        // when the project's default mode is session-driven).
        if (mode == LoopMode.Session && IsCodexTab) return false;
        foreach (var r in Specs) if (r.IsSelected) return true;
        return false;
    }

    private void SetDefault(LoopMode mode)
    {
        if (_tab is null) return;
        try
        {
            // The .drover folder may not exist yet — Initialize creates it
            // alongside the spec template.
            if (!_templateService.IsInitialized(_tab.Project.Path))
                _templateService.Initialize(_tab.Project.Path);
            _loopConfig.SetDefaultMode(_tab.Project.Path, mode);
            DefaultMode = mode;
            UpdateButtonLabel();
            StatusText = $"Default loop mode set to {ModeLabel(mode)}.";
        }
        catch (System.Exception ex)
        {
            StatusText = $"Set default failed: {ex.Message}";
        }
    }

    private void StartLoop(LoopMode mode)
    {
        if (_tab is null) return;
        var session = _tab;
        var selected = new System.Collections.Generic.List<SpecFile>();
        foreach (var r in Specs) if (r.IsSelected) selected.Add(r.File);
        if (selected.Count == 0) return;

        // Specs always run in id-ascending order. The selection order in the
        // ListBox is irrelevant.
        selected.Sort((a, b) => string.Compare(a.Id, b.Id, System.StringComparison.OrdinalIgnoreCase));

        // Make sure the .drover scaffold is present before sending the prompt
        // (the prompts read from .drover/prompts/ and the loop-batch prompt
        // expects execute-spec.md to exist on disk).
        try
        {
            if (!_templateService.IsInitialized(_tab.Project.Path))
                _templateService.Initialize(_tab.Project.Path);
        }
        catch (System.Exception ex)
        {
            StatusText = $"Initialize specs failed: {ex.Message}";
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        IsLoopRunning = true;
        ProgressText = $"Starting loop ({ModeLabel(mode)}, {selected.Count} spec{(selected.Count == 1 ? "" : "s")})…";
        StatusText = string.Empty;
        RaiseCommandStates();

        var progress = new System.Progress<LoopEvent>(OnLoopEvent);
        var sessionAdapter = new TerminalTabSession(session);

        var agentKind = HookAgentKinds.FromProjectKind(_tab.Project.Kind) ?? HookAgentKind.Claude;
        ILoopRunner runner = mode switch
        {
            LoopMode.Session => new SessionDrivenLoopRunner(_prompts, agentKind),
            _                => new DroverDrivenLoopRunner(_prompts, agentKind),
        };

        var token = _cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await runner.RunAsync(selected, _tab.Project.Path, sessionAdapter, progress, token).ConfigureAwait(false);
            }
            catch (System.Exception ex)
            {
                _ = _dispatcher.BeginInvoke(new System.Action(() =>
                {
                    StatusText = $"Loop crashed: {ex.Message}";
                }));
            }
            finally
            {
                sessionAdapter.Dispose();
                _ = _dispatcher.BeginInvoke(new System.Action(() =>
                {
                    IsLoopRunning = false;
                    RaiseCommandStates();
                }));
            }
        });
    }

    private void StopLoop()
    {
        _cts?.Cancel();
        ProgressText = "Stopping…";
    }

    private void OnLoopEvent(LoopEvent evt)
    {
        // System.Progress<T> already marshals to the captured SynchronizationContext
        // (the UI thread, since it was constructed on the UI thread). Update VM
        // state directly.
        ProgressText = FormatEvent(evt);
        if (evt.Kind == LoopEventKind.Stopped || evt.Kind == LoopEventKind.Errored)
        {
            // Final reports come right before the runner finishes — leave
            // ProgressText as the terminal message; IsLoopRunning is cleared
            // in the Task continuation so the button re-enables.
        }
    }

    private static string FormatEvent(LoopEvent e) => e.Kind switch
    {
        LoopEventKind.Started       => $"Running {e.Total} spec{(e.Total == 1 ? "" : "s")}…",
        LoopEventKind.SpecStarted   => $"Running {e.Index} of {e.Total} · {e.Message ?? e.SpecId}",
        LoopEventKind.SpecCompleted => $"Done {e.Index} of {e.Total} · {e.Message ?? e.SpecId}",
        LoopEventKind.SpecBlocked   => $"Blocked {e.Index} of {e.Total} · {e.Message ?? e.SpecId}",
        LoopEventKind.Stopped       => string.IsNullOrEmpty(e.Message) ? "Stopped." : $"Stopped: {e.Message}",
        LoopEventKind.Errored       => string.IsNullOrEmpty(e.Message) ? "Errored." : $"Errored: {e.Message}",
        _                           => string.Empty,
    };

    private void UpdateButtonLabel()
    {
        RunLoopButtonLabel = $"Run loop ({ModeLabel(DefaultMode)})";
    }

    private static string ModeLabel(LoopMode mode) => mode == LoopMode.Session ? "session" : "Drover";

    private void RaiseCommandStates()
    {
        ((RelayCommand)RunLoopDefaultCommand).NotifyCanExecuteChanged();
        ((RelayCommand)RunLoopDroverCommand).NotifyCanExecuteChanged();
        ((RelayCommand)RunLoopSessionCommand).NotifyCanExecuteChanged();
        ((RelayCommand)StopLoopCommand).NotifyCanExecuteChanged();
    }

    partial void OnIsLoopRunningChanged(bool value) => RaiseCommandStates();

    partial void OnDefaultModeChanged(LoopMode value)
    {
        UpdateButtonLabel();
        UpdatePrimaryRunLoopState();
        RaiseCommandStates();
    }
}
