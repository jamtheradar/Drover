using System.Collections.ObjectModel;
using Drover.App.Models;
using Drover.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Drover.App.ViewModels;

public sealed record ModelSpendRow(string Model, string CostText);
public sealed record DailyBarRow(string Label, double Cost, string CostText, double BarHeight, bool IsToday);
public sealed record ProjectSpendRow(string Project, string CostText, string TokensText, double BarFraction);
public sealed record RecentSessionRow(string TimeText, string Project, string Model, string CostText, string TokensText);

public sealed partial class ShellViewModel : ObservableObject
{
    private readonly ProjectsCatalog _catalog;
    private readonly SettingsStore _settings;
    private readonly ActivityLog _activity;
    private readonly HooksGateway _hooks;
    private readonly HooksInstaller _hooksInstaller;
    private readonly GitStatusService _gitStatus;
    private readonly LiveTodoStore _liveTodos;
    private readonly AgentCatalog _agents;
    private readonly BuildStatusTracker _buildTracker;
    private readonly GateRegistry _gates;
    private readonly ToastNotifier _toaster;
    private readonly TemplateService _templateService;
    private readonly PromptLibrary _promptLibrary;
    private readonly SkillsRepoCatalog _skillsRepoCatalog;
    private readonly VaultStore _vaultStore;
    private readonly VaultMcpInstaller _vaultMcpInstaller;
    private readonly System.Windows.Threading.DispatcherTimer _hooksRecheckTimer;

    [ObservableProperty] private TerminalTabViewModel? _selectedTab;
    [ObservableProperty] private TerminalTabViewModel? _secondaryTab;
    [ObservableProperty] private string _statusText = "ready";
    [ObservableProperty] private bool _activityVisible;
    [ObservableProperty] private bool _planPanelVisible;
    [ObservableProperty] private bool _taskPanelVisible;
    [ObservableProperty] private bool _fileExplorerPanelVisible;
    [ObservableProperty] private bool _kanbanPanelVisible;
    [ObservableProperty] private bool _specsPanelVisible;
    [ObservableProperty] private bool _vaultPanelVisible;
    /// <summary>
    /// Pixel width of the right pane (Activity, Plan, or Tasks, whichever is open).
    /// 0 when collapsed. Bound two-way to the right column so the GridSplitter
    /// can adjust it directly. Restored from <see cref="_lastRightPaneOpenWidth"/>
    /// when a panel is re-opened.
    /// </summary>
    [ObservableProperty] private double _rightPaneWidth;
    private double _lastRightPaneOpenWidth = 380;
    public bool RightPaneOpen => PlanPanelVisible || ActivityVisible || TaskPanelVisible || FileExplorerPanelVisible || KanbanPanelVisible || SpecsPanelVisible || VaultPanelVisible;

    public KanbanViewModel Kanban { get; private set; } = null!;

    public ObservableCollection<Drover.App.Models.FileNode> FileExplorerRoots { get; } = new();
    [ObservableProperty] private string _fileExplorerStatusText = string.Empty;
    private FileExplorerWatcher? _fileExplorerWatcher;
    [ObservableProperty] private Drover.App.Models.PlanDocument? _currentPlan;
    [ObservableProperty] private Drover.App.Models.PlanFileEntry? _selectedPlan;
    [ObservableProperty] private bool _hasAnyPlan;
    [ObservableProperty] private string _planStatusText = string.Empty;
    [ObservableProperty] private bool _planEditMode;
    [ObservableProperty] private string _planEditBuffer = string.Empty;
    [ObservableProperty] private bool _selectedPlanIsDone;
    [ObservableProperty] private bool _canMarkSelectedPlanDone;
    /// <summary>Font multiplier for the plan panel content. 1.0 = default.
    /// Applied as a LayoutTransform so everything (text, padding) scales together.</summary>
    [ObservableProperty] private double _planFontScale = 1.0;
    /// <summary>Multiplier applied to all non-terminal panels (Specs, Plan,
    /// Tasks, File explorer, Kanban, Vault, dashboard). Sourced from
    /// <see cref="AppSettings.PanelFontSize"/> divided by 12 (the design
    /// baseline). Persisted back to settings whenever the user adjusts it
    /// from the Settings window.</summary>
    [ObservableProperty] private double _panelFontScale = 1.0;
    public ObservableCollection<Drover.App.Models.PlanFileEntry> AvailablePlans { get; } = new();

    // ─── Task panel state (parallels Plan panel) ────────────────────────────
    [ObservableProperty] private Drover.App.Models.TaskDocument? _currentTaskList;
    [ObservableProperty] private Drover.App.Models.TaskFileEntry? _selectedTaskFile;
    [ObservableProperty] private bool _hasAnyTaskFile;
    [ObservableProperty] private string _taskListStatusText = string.Empty;
    [ObservableProperty] private bool _taskEditMode;
    [ObservableProperty] private string _taskEditBuffer = string.Empty;
    [ObservableProperty] private bool _selectedTaskFileIsDone;
    [ObservableProperty] private bool _canMarkSelectedTaskFileDone;
    [ObservableProperty] private double _taskFontScale = 1.0;
    [ObservableProperty] private bool _taskComposerVisible;
    [ObservableProperty] private string _taskComposerText = string.Empty;
    public ObservableCollection<Drover.App.Models.TaskFileEntry> AvailableTaskFiles { get; } = new();
    [ObservableProperty] private bool _dashboardActive;
    [ObservableProperty] private bool _tokenomicsActive;
    [ObservableProperty] private bool _memoryActive;
    [ObservableProperty] private string? _selectedMemoryName;
    [ObservableProperty] private string? _selectedMemoryPath;
    [ObservableProperty] private string _selectedMemoryContent = string.Empty;
    [ObservableProperty] private bool _hasNoMemory;
    [ObservableProperty] private bool _memoryEditMode;
    [ObservableProperty] private bool _memoryDirty;
    [ObservableProperty] private System.Windows.Documents.FlowDocument? _memoryRendered;
    [ObservableProperty] private string _memorySaveStatus = string.Empty;

    partial void OnSelectedMemoryContentChanged(string value)
    {
        if (_suppressDirty) return;
        MemoryDirty = true;
        MemorySaveStatus = string.Empty;
    }
    [ObservableProperty] private string _sessionUsageText = "Session idle";
    [ObservableProperty] private string _sessionResetText = string.Empty;
    [ObservableProperty] private double _sessionPercent;
    [ObservableProperty] private bool _sessionActive;
    // True once any tab has pushed CC's authoritative rate_limits block via
    // statusLine. While true, App.xaml.cs leaves SessionUsageText / SessionPercent
    // / SessionResetText alone — the local USD-budget proxy is only a fallback.
    [ObservableProperty] private bool _sessionFromCc;
    [ObservableProperty] private string _dailyCostText = "Today $0.00";
    [ObservableProperty] private string _dailyTokensText = "0 in / 0 out";
    [ObservableProperty] private string _dailyCacheText = "0 cache";
    [ObservableProperty] private string _weekCostText = "$0.00";
    [ObservableProperty] private string _monthCostText = "$0.00";
    [ObservableProperty] private string _allTimeCostText = "$0.00";
    [ObservableProperty] private string _allTimeTokensText = "0 in / 0 out / 0 cache";
    public ObservableCollection<ModelSpendRow> ModelSpend { get; } = new();
    public ObservableCollection<DailyBarRow> DailyBars { get; } = new();
    public ObservableCollection<ProjectSpendRow> ProjectSpend { get; } = new();
    public ObservableCollection<ModelSpendRow> ModelSpend7d { get; } = new();
    public ObservableCollection<RecentSessionRow> RecentSessions { get; } = new();
    public ObservableCollection<MemoryFile> UserMemoryFiles { get; } = new();
    public ObservableCollection<MemoryFile> ProjectMemoryFiles { get; } = new();

    partial void OnDashboardActiveChanged(bool value)
    {
        if (value) TokenomicsActive = false;
    }

    partial void OnActivityVisibleChanged(bool value)
    {
        if (value) { PlanPanelVisible = false; TaskPanelVisible = false; FileExplorerPanelVisible = false; KanbanPanelVisible = false; SpecsPanelVisible = false; VaultPanelVisible = false; }
        UpdateRightPaneWidth();
        OnPropertyChanged(nameof(RightPaneOpen));
    }

    partial void OnSpecsPanelVisibleChanged(bool value)
    {
        if (value)
        {
            ActivityVisible = false;
            PlanPanelVisible = false;
            TaskPanelVisible = false;
            FileExplorerPanelVisible = false;
            KanbanPanelVisible = false;
            VaultPanelVisible = false;
            // Bind the panel to the current tab on first show — same pattern
            // as Kanban.OnTabChanged.
            Specs.OnTabChanged(SelectedTab);
        }
        UpdateRightPaneWidth();
        OnPropertyChanged(nameof(RightPaneOpen));
    }

    partial void OnPlanPanelVisibleChanged(bool value)
    {
        if (value)
        {
            ActivityVisible = false;
            TaskPanelVisible = false;
            FileExplorerPanelVisible = false;
            KanbanPanelVisible = false;
            SpecsPanelVisible = false;
            VaultPanelVisible = false;
            LoadPlanForSelectedTab();
        }
        else
        {
            _planWatcher?.Stop();
        }
        UpdateRightPaneWidth();
        OnPropertyChanged(nameof(RightPaneOpen));
    }

    partial void OnTaskPanelVisibleChanged(bool value)
    {
        if (value)
        {
            ActivityVisible = false;
            PlanPanelVisible = false;
            FileExplorerPanelVisible = false;
            KanbanPanelVisible = false;
            SpecsPanelVisible = false;
            VaultPanelVisible = false;
            LoadTaskListForSelectedTab();
        }
        else
        {
            _taskWatcher?.Stop();
        }
        UpdateRightPaneWidth();
        OnPropertyChanged(nameof(RightPaneOpen));
    }

    partial void OnFileExplorerPanelVisibleChanged(bool value)
    {
        if (value)
        {
            ActivityVisible = false;
            PlanPanelVisible = false;
            TaskPanelVisible = false;
            KanbanPanelVisible = false;
            SpecsPanelVisible = false;
            VaultPanelVisible = false;
            LoadFileTreeForSelectedTab();
        }
        else
        {
            _fileExplorerWatcher?.Stop();
        }
        UpdateRightPaneWidth();
        OnPropertyChanged(nameof(RightPaneOpen));
    }

    partial void OnKanbanPanelVisibleChanged(bool value)
    {
        if (value)
        {
            ActivityVisible = false;
            PlanPanelVisible = false;
            TaskPanelVisible = false;
            FileExplorerPanelVisible = false;
            SpecsPanelVisible = false;
            VaultPanelVisible = false;
            // Re-activate the current source against the active tab. The ctor primed
            // CurrentSource at restore time; we just need to retarget the tab now.
            Kanban.OnTabChanged(SelectedTab);
            Kanban.RebuildColumns();
        }
        UpdateRightPaneWidth();
        OnPropertyChanged(nameof(RightPaneOpen));
    }

    partial void OnVaultPanelVisibleChanged(bool value)
    {
        if (value)
        {
            ActivityVisible = false;
            PlanPanelVisible = false;
            TaskPanelVisible = false;
            FileExplorerPanelVisible = false;
            KanbanPanelVisible = false;
            SpecsPanelVisible = false;
            // Bind the vault to the active project on every show so opening the
            // panel from the sidebar after switching tabs targets the right namespace.
            SyncVaultActiveProject();
        }
        UpdateRightPaneWidth();
        OnPropertyChanged(nameof(RightPaneOpen));
    }

    private void SyncVaultActiveProject()
    {
        var path = SelectedTab?.Project?.Path;
        _vaultStore.SetActiveProject(string.IsNullOrEmpty(path) ? null : path);
        Vault.OnActiveProjectChanged();
    }

    partial void OnPanelFontScaleChanged(double value)
    {
        // Persist back to settings as a point size so it round-trips with the
        // existing FontSize knob in Settings → Appearance. Clamp to the same
        // range the per-panel +/- buttons use; the dropdown enforces it too.
        var clamped = System.Math.Clamp(value, 0.7, 2.5);
        if (System.Math.Abs(clamped - value) > 0.0001) { PanelFontScale = clamped; return; }
        OnPropertyChanged(nameof(PanelFontPercentText));
        var size = (int)System.Math.Round(clamped * 12.0);
        if (size <= 0) size = 12;
        if (_settings.Current.PanelFontSize == size) return;
        _settings.Update(_settings.Current with { PanelFontSize = size });
    }

    /// <summary>
    /// Two-way string surface for the status-bar zoom ComboBox. Source of
    /// truth is <see cref="PanelFontScale"/>; this property formats it as
    /// "125%" and parses typed/selected percentages back into the scale.
    /// Editable ComboBoxes have a fragile relationship with value-converter
    /// bindings on <c>Text</c> (the converter ConvertBack only fires on
    /// LostFocus and selection-via-dropdown doesn't always re-fire it), so
    /// we expose a plain string property and do the parse here.
    /// </summary>
    public string PanelFontPercentText
    {
        get => $"{(int)System.Math.Round(PanelFontScale * 100.0)}%";
        set
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var trimmed = value.Trim().TrimEnd('%').Trim();
            if (!double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var pct)
                && !double.TryParse(trimmed, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.CurrentCulture, out pct))
                return;
            var scale = System.Math.Clamp(System.Math.Round(pct) / 100.0, 0.7, 2.5);
            if (System.Math.Abs(scale - PanelFontScale) < 0.0001)
            {
                // Re-emit in case user typed something the formatter rounds to
                // the current scale (e.g. "100" when scale is already 1.0) so
                // the box snaps back to a canonical "100%" string.
                OnPropertyChanged(nameof(PanelFontPercentText));
                return;
            }
            PanelFontScale = scale;
        }
    }

    partial void OnRightPaneWidthChanged(double value)
    {
        // GridSplitter writes here when the user drags. Remember the opened width
        // so re-toggling on later restores it. Only remember while a panel is open;
        // when collapsed the column is 0 and that's not a width worth saving.
        if (RightPaneOpen && value > 0) _lastRightPaneOpenWidth = value;
    }

    private void UpdateRightPaneWidth()
    {
        if (RightPaneOpen)
        {
            // Restore last opened width, clamped sensibly.
            var w = _lastRightPaneOpenWidth;
            if (w < 240) w = 240;
            if (w > 1000) w = 1000;
            RightPaneWidth = w;
        }
        else
        {
            RightPaneWidth = 0;
        }
    }

    partial void OnSelectedTabChanged(TerminalTabViewModel? value)
    {
        if (PlanPanelVisible) LoadPlanForSelectedTab();
        if (TaskPanelVisible) LoadTaskListForSelectedTab();
        if (FileExplorerPanelVisible) LoadFileTreeForSelectedTab();
        if (KanbanPanelVisible) Kanban.OnTabChanged(value);
        if (SpecsPanelVisible) Specs.OnTabChanged(value);
        if (VaultPanelVisible) SyncVaultActiveProject();
    }

    partial void OnTokenomicsActiveChanged(bool value)
    {
        if (value) { DashboardActive = false; MemoryActive = false; }
    }

    partial void OnMemoryActiveChanged(bool value)
    {
        if (value)
        {
            DashboardActive = false;
            TokenomicsActive = false;
            RefreshMemory();
        }
    }

    public SpecsPanelViewModel Specs { get; }
    public VaultViewModel Vault { get; }

    public ShellViewModel(ProjectsCatalog catalog, SettingsStore settings, ActivityLog activity, HooksGateway hooks, HooksInstaller hooksInstaller, GitStatusService gitStatus, LiveTodoStore liveTodos, AgentCatalog agents, BuildStatusTracker buildTracker, GateRegistry gates, ToastNotifier toaster, TemplateService templateService, PromptLibrary promptLibrary, SpecsPanelViewModel specs, SkillsRepoCatalog skillsRepoCatalog, VaultStore vaultStore, VaultMcpInstaller vaultMcpInstaller, VaultViewModel vault)
    {
        _catalog = catalog;
        _settings = settings;
        _activity = activity;
        _hooks = hooks;
        _hooksInstaller = hooksInstaller;
        _gitStatus = gitStatus;
        _liveTodos = liveTodos;
        _agents = agents;
        _buildTracker = buildTracker;
        _gates = gates;
        _toaster = toaster;
        _templateService = templateService;
        _promptLibrary = promptLibrary;
        _skillsRepoCatalog = skillsRepoCatalog;
        _vaultStore = vaultStore;
        _vaultMcpInstaller = vaultMcpInstaller;
        Specs = specs;
        Vault = vault;
        Kanban = new KanbanViewModel(liveTodos, agents, gates, toaster, settings);

        // Seed the panel-font multiplier from persisted settings. 12pt is the
        // design baseline for panels, so size N -> scale N/12. Clamp to the
        // same bounds the per-panel +/- buttons enforce so a stale value can't
        // make the UI unreadable.
        var pfs = _settings.Current.PanelFontSize;
        if (pfs <= 0) pfs = 12;
        _panelFontScale = System.Math.Clamp(pfs / 12.0, 0.7, 2.5);

        // Pick up out-of-band edits from the Settings dialog. Marshal to the UI
        // dispatcher because Changed fires on whatever thread called Update.
        _settings.Changed += (_, _) =>
        {
            var size = _settings.Current.PanelFontSize;
            if (size <= 0) size = 12;
            var scale = System.Math.Clamp(size / 12.0, 0.7, 2.5);
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) PanelFontScale = scale;
            else dispatcher.BeginInvoke(new System.Action(() => PanelFontScale = scale));
        };

        // Project list mirror — wraps the persisted ProjectDefinition records with
        // the reactive git-status chip the sidebar/welcome views render. Kept in
        // lock-step with _catalog.Projects via CollectionChanged below.
        foreach (var p in _catalog.Projects)
        {
            ProjectItems.Add(new ProjectListItem(p));
            _gitStatus.Watch(p.Path);
        }
        _catalog.Projects.CollectionChanged += OnCatalogChanged;
        _gitStatus.StatusChanged += OnGitStatusChanged;

        // Periodic recheck so the hook-status badge picks up out-of-band edits to
        // settings.json files (user manually removed hooks, edited settings.local.json,
        // ran another tool that rewrote them). 30s is fine — hook state rarely changes.
        _hooksRecheckTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = System.TimeSpan.FromSeconds(30),
        };
        _hooksRecheckTimer.Tick += (_, _) => RefreshAllHooksStatus();
        _hooksRecheckTimer.Start();
        OpenProjectCommand = new RelayCommand<ProjectDefinition>(OpenProject);
        CloseTabCommand = new RelayCommand<TerminalTabViewModel>(CloseTab);
        RemoveProjectCommand = new RelayCommand<ProjectDefinition>(RemoveProject);
        ToggleActivityCommand = new RelayCommand(() => ActivityVisible = !ActivityVisible);
        TogglePlanPanelCommand = new RelayCommand(() => PlanPanelVisible = !PlanPanelVisible);
        RefreshPlanCommand = new RelayCommand(LoadPlanForSelectedTab);
        SendTaskCommand = new RelayCommand<Drover.App.Models.PlanTask>(SendTask);
        SendSectionCommand = new RelayCommand<Drover.App.Models.PlanSection>(SendSection);
        ReadPlanAndCreateTasksCommand = new RelayCommand(ReadPlanAndCreateTasks);
        CreatePlanFromTemplateCommand = new RelayCommand(CreatePlanFromTemplate);
        OpenPlanFileCommand = new RelayCommand(OpenPlanFile);
        TogglePlanEditCommand = new RelayCommand(TogglePlanEdit);
        SavePlanEditCommand = new RelayCommand(SavePlanEdit);
        CancelPlanEditCommand = new RelayCommand(() => { PlanEditMode = false; PlanEditBuffer = string.Empty; });
        MarkSelectedPlanDoneCommand = new RelayCommand(MarkSelectedPlanDone);
        ReactivateSelectedPlanCommand = new RelayCommand(ReactivateSelectedPlan);
        PlanFontScaleUpCommand = new RelayCommand(() => PlanFontScale = System.Math.Min(2.5, System.Math.Round((PlanFontScale + 0.1) * 100) / 100));
        PlanFontScaleDownCommand = new RelayCommand(() => PlanFontScale = System.Math.Max(0.7, System.Math.Round((PlanFontScale - 0.1) * 100) / 100));
        PlanFontScaleResetCommand = new RelayCommand(() => PlanFontScale = 1.0);

        ZoomPanelsInCommand = new RelayCommand(() => PanelFontScale = System.Math.Min(2.5, System.Math.Round((PanelFontScale + 0.05) * 100) / 100));
        ZoomPanelsOutCommand = new RelayCommand(() => PanelFontScale = System.Math.Max(0.7, System.Math.Round((PanelFontScale - 0.05) * 100) / 100));
        ResetPanelZoomCommand = new RelayCommand(() => PanelFontScale = 1.0);

        ToggleTaskPanelCommand = new RelayCommand(() => TaskPanelVisible = !TaskPanelVisible);
        ToggleFileExplorerPanelCommand = new RelayCommand(() => FileExplorerPanelVisible = !FileExplorerPanelVisible);
        ToggleKanbanPanelCommand = new RelayCommand(() => KanbanPanelVisible = !KanbanPanelVisible);
        ToggleSpecsPanelCommand = new RelayCommand(() => SpecsPanelVisible = !SpecsPanelVisible);
        ToggleVaultPanelCommand = new RelayCommand(() => VaultPanelVisible = !VaultPanelVisible);
        RefreshFileExplorerCommand = new RelayCommand(LoadFileTreeForSelectedTab);
        RefreshTaskListCommand = new RelayCommand(LoadTaskListForSelectedTab);
        SendTaskItemCommand = new RelayCommand<Drover.App.Models.TaskItem>(SendTaskItem);
        SendTaskSectionCommand = new RelayCommand<Drover.App.Models.TaskSection>(SendTaskSection);
        CreateTaskFileFromTemplateCommand = new RelayCommand(CreateTaskFileFromTemplate);
        OpenTaskFileCommand = new RelayCommand(OpenTaskFile);
        ToggleTaskEditCommand = new RelayCommand(ToggleTaskEdit);
        SaveTaskEditCommand = new RelayCommand(SaveTaskEdit);
        CancelTaskEditCommand = new RelayCommand(() => { TaskEditMode = false; TaskEditBuffer = string.Empty; });
        MarkSelectedTaskFileDoneCommand = new RelayCommand(MarkSelectedTaskFileDone);
        ReactivateSelectedTaskFileCommand = new RelayCommand(ReactivateSelectedTaskFile);
        TaskFontScaleUpCommand = new RelayCommand(() => TaskFontScale = System.Math.Min(2.5, System.Math.Round((TaskFontScale + 0.1) * 100) / 100));
        TaskFontScaleDownCommand = new RelayCommand(() => TaskFontScale = System.Math.Max(0.7, System.Math.Round((TaskFontScale - 0.1) * 100) / 100));
        TaskFontScaleResetCommand = new RelayCommand(() => TaskFontScale = 1.0);
        BeginAddTaskCommand = new RelayCommand(BeginAddTask);
        CommitAddTaskCommand = new RelayCommand(CommitAddTask);
        CancelAddTaskCommand = new RelayCommand(CancelAddTask);
        SplitRightCommand = new RelayCommand<TerminalTabViewModel>(SplitRight);
        CloseSecondaryCommand = new RelayCommand(CloseSecondary);
        ShowDashboardCommand = new RelayCommand(() => DashboardActive = !DashboardActive);
        ShowTokenomicsCommand = new RelayCommand(() => TokenomicsActive = !TokenomicsActive);
        ShowMemoryCommand = new RelayCommand(() => MemoryActive = !MemoryActive);
        BackToSessionCommand = new RelayCommand(() => { DashboardActive = false; TokenomicsActive = false; MemoryActive = false; });
        RefreshMemoryCommand = new RelayCommand(RefreshMemory);
        SelectMemoryCommand = new RelayCommand<MemoryFile>(SelectMemory);
        ToggleMemoryEditCommand = new RelayCommand(() => MemoryEditMode = !MemoryEditMode);
        SaveMemoryCommand = new RelayCommand(SaveMemory);
        FocusTabCommand = new RelayCommand<TerminalTabViewModel>(FocusTab);
        OpenTabFolderCommand = new RelayCommand<TerminalTabViewModel>(OpenTabFolder);

        InitializeSpecsCommand = new RelayCommand<ProjectDefinition>(InitializeSpecs);
        EnhanceSpecTemplateCommand = new RelayCommand<ProjectDefinition>(EnhanceSpecTemplate);
        OpenDroverFolderCommand = new RelayCommand<ProjectDefinition>(OpenDroverFolder);
        PromoteInterrogateCommand = new RelayCommand<Drover.App.Models.TaskItem>(PromoteInterrogate);
    }

    public RelayCommand<ProjectDefinition> InitializeSpecsCommand { get; }
    public RelayCommand<ProjectDefinition> EnhanceSpecTemplateCommand { get; }
    public RelayCommand<ProjectDefinition> OpenDroverFolderCommand { get; }
    public RelayCommand<Drover.App.Models.TaskItem> PromoteInterrogateCommand { get; }

    /// <summary>
    /// First active tab whose project root matches <paramref name="projectRoot"/>
    /// (case-insensitive). Returns null when no tab is open for the project.
    /// </summary>
    private TerminalTabViewModel? FindTabForProject(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot)) return null;
        foreach (var t in Tabs)
        {
            if (string.Equals(t.Project.Path, projectRoot, System.StringComparison.OrdinalIgnoreCase))
                return t;
        }
        return null;
    }

    private void InitializeSpecs(ProjectDefinition? project)
    {
        if (project is null || string.IsNullOrEmpty(project.Path)) return;
        try
        {
            _templateService.Initialize(project.Path);
        }
        catch (System.Exception ex)
        {
            StatusText = $"Initialize specs failed: {ex.Message}";
            return;
        }
        StatusText = "Drover initialized. Edit .drover/spec-template.md or run Enhance spec template to tailor it.";
    }

    private void EnhanceSpecTemplate(ProjectDefinition? project)
    {
        if (project is null || string.IsNullOrEmpty(project.Path)) return;
        if (!_templateService.IsInitialized(project.Path))
        {
            StatusText = "Run Initialize specs first.";
            return;
        }
        var tab = FindTabForProject(project.Path);
        if (tab is null)
        {
            StatusText = "Open a Claude Code session for this project first.";
            return;
        }
        string prompt;
        try
        {
            prompt = _promptLibrary.Resolve("enhance-template", project.Path);
        }
        catch (System.Exception ex)
        {
            StatusText = $"Enhance prompt failed: {ex.Message}";
            return;
        }
        SelectedTab = tab;
        tab.SendInput(prompt);
        StatusText = "Sent: enhance spec template.";
    }

    /// <summary>
    /// Opens the Skills browser window for <paramref name="project"/>. Resolves
    /// (or opens) the project's terminal tab so Install can route
    /// <c>npx skills add &lt;source&gt;</c> into a known shell, then shows
    /// <see cref="Drover.App.Views.SkillsWindow"/> modelessly.
    /// </summary>
    public void OpenSkillsWindow(ProjectDefinition? project)
    {
        if (project is null) return;
        var tab = FindTabForProject(project.Path);
        if (tab is null)
        {
            OpenProject(project);
            tab = FindTabForProject(project.Path);
            if (tab is null)
            {
                StatusText = $"Couldn't open a tab for {project.Name}.";
                return;
            }
        }
        SelectedTab = tab;
        var win = new Drover.App.Views.SkillsWindow(project, tab, _skillsRepoCatalog, _settings)
        {
            Owner = System.Windows.Application.Current?.MainWindow,
        };
        win.Show();
    }

    /// <summary>
    /// Writes the <c>drover-vault</c> MCP server entry into
    /// <c>&lt;project&gt;/.mcp.json</c> so any Claude Code session in
    /// that project can reach the vault. Idempotent: re-running on a
    /// project where the entry already matches is a no-op (toast says
    /// "already registered"), and re-running after the exe path
    /// changed updates <c>args[1]</c>.
    /// </summary>
    public void EnableVaultForProject(ProjectDefinition? project)
    {
        if (project is null || string.IsNullOrEmpty(project.Path)) return;
        try
        {
            var result = _vaultMcpInstaller.Install(project.Path);
            string body;
            if (result.WasFresh)
                body = $"Vault MCP server registered for {project.Name}. Restart Claude Code sessions to pick it up.";
            else if (result.WasUpdated)
                body = "Vault MCP server registration updated.";
            else
                body = "Vault MCP server already registered.";

            StatusText = body;
            _toaster.NotifyInfo("Vault MCP", body);
        }
        catch (System.Exception ex)
        {
            var message = $"Enable vault failed: {ex.Message}";
            StatusText = message;
            _toaster.NotifyInfo("Vault MCP", message);
        }
    }

    private void OpenDroverFolder(ProjectDefinition? project)
    {
        if (project is null || string.IsNullOrEmpty(project.Path)) return;
        var path = System.IO.Path.Combine(project.Path, ".drover");
        if (!System.IO.Directory.Exists(path))
        {
            StatusText = ".drover folder doesn't exist yet — run Initialize specs first.";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            StatusText = $"Open .drover failed: {ex.Message}";
        }
    }

    private void PromoteInterrogate(Drover.App.Models.TaskItem? item)
    {
        if (item is null || SelectedTab is null) return;
        var project = SelectedTab.Project;
        if (string.IsNullOrEmpty(project.Path)) return;

        // Make sure the .drover/ scaffold is present before sending the prompt — the
        // prompt's first instruction is to read .drover/spec-template.md, which would
        // dead-end if the user never ran Initialize specs.
        if (!_templateService.IsInitialized(project.Path))
        {
            try { _templateService.Initialize(project.Path); }
            catch (System.Exception ex)
            {
                StatusText = $"Initialize specs failed: {ex.Message}";
                return;
            }
        }

        // Pass the task line literally — Claude needs to match it back to the file.
        var rawLine = string.IsNullOrEmpty(item.RawLine) ? item.Text : item.RawLine;
        string prompt;
        try
        {
            prompt = _promptLibrary.Resolve(
                "promote-interrogate",
                project.Path,
                new System.Collections.Generic.Dictionary<string, string> { ["TASK_LINE"] = rawLine });
        }
        catch (System.Exception ex)
        {
            StatusText = $"Promote prompt failed: {ex.Message}";
            return;
        }
        SelectedTab.SendInput(prompt);
        var preview = item.Text.Length <= 40 ? item.Text : item.Text.Substring(0, 40) + "…";
        TaskListStatusText = "Promoted: " + preview;
    }

    public RelayCommand<TerminalTabViewModel> OpenTabFolderCommand { get; }

    /// <summary>
    /// Fires every time a tab pushes a Claude Code statusLine update. App.xaml.cs uses this
    /// to trigger a TokenStats refresh so polled fields (effort, context %) update on the same
    /// 10s cadence as CC's push instead of waiting up to 15s for the next polling tick.
    /// </summary>
    public event System.EventHandler? StatusLinePushed;

    /// <summary>
    /// Aggregates Claude Code's account-wide rate-limit numbers from any tab's most-recent
    /// statusLine push into the shell-level Session* properties. CC's numbers are
    /// authoritative (server-side), so once we've seen them we override the local USD-budget
    /// proxy. The five_hour window matches the existing top-bar "session" pill semantics.
    /// </summary>
    public void OnTabStatusLineUpdated(object? sender, System.EventArgs e)
    {
        if (sender is not TerminalTabViewModel tab) return;

        // Trigger downstream refresh first — every CC push, no matter what fields it carries,
        // is a signal that the user just had activity and any polled metric (notably effort,
        // which CC doesn't include in StatusJSON) is worth recomputing.
        StatusLinePushed?.Invoke(this, System.EventArgs.Empty);

        // Only adopt rate-limit values when CC actually pushed a non-zero block — a brand-new
        // session before any usage is reported as 0%. We treat 0% with a non-empty resets-at
        // string as valid (server has anchored the window) and 0% with empty reset as "no data".
        var hasFiveHour = !string.IsNullOrEmpty(tab.CcRateLimit5hResetText) || tab.CcRateLimit5hPercent > 0;
        if (!hasFiveHour) return;

        SessionFromCc = true;
        SessionPercent = tab.CcRateLimit5hPercent;
        SessionActive = true;
        SessionUsageText = $"Session {tab.CcRateLimit5hPercent:0}%";
        SessionResetText = string.IsNullOrEmpty(tab.CcRateLimit5hResetText)
            ? string.Empty
            : tab.CcRateLimit5hResetText;
    }

    /// <summary>
    /// Opens the gateway's JSONL hook log in the user's default editor for that
    /// extension. Useful for diagnosing hook routing — every received event is
    /// recorded with timestamp, session, type, tool, phase, and whether it was
    /// routed to a tab handler.
    /// </summary>
    public void OpenHooksLog()
    {
        var path = _hooks.LogPath;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path))
        {
            StatusText = "no hook events recorded yet — try a Claude turn";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            StatusText = $"open hooks log failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Types `@<relativePath> ` into the active tab's terminal without sending Enter.
    /// Claude Code interprets `@path` references natively, so this is the cheapest way
    /// to hand a file to the model from the explorer.
    /// </summary>
    public void InsertAtPathReference(Drover.App.Models.FileNode? node)
    {
        if (node is null || SelectedTab is null) return;
        var rel = node.RelativePath;
        if (string.IsNullOrEmpty(rel) || rel == ".") return;
        // Quote when there's whitespace; @paths are space-delimited otherwise.
        var token = rel.Contains(' ') ? $"@\"{rel}\" " : $"@{rel} ";
        SelectedTab.SendInput(token, appendReturn: false);
        FileExplorerStatusText = $"inserted @{rel}";
    }

    /// <summary>Opens the OS default handler for a file (or folder) selected in the file explorer.</summary>
    public void OpenFileNode(Drover.App.Models.FileNode? node)
    {
        if (node is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(node.FullPath) { UseShellExecute = true });
        }
        catch (System.Exception ex)
        {
            FileExplorerStatusText = $"open failed: {ex.Message}";
        }
    }

    /// <summary>Reveals a file or folder in Windows Explorer (selected when possible).</summary>
    public void RevealFileNode(Drover.App.Models.FileNode? node)
    {
        if (node is null) return;
        try
        {
            if (node.IsDirectory)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(node.FullPath) { UseShellExecute = true });
            }
            else
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{node.FullPath}\"");
            }
        }
        catch (System.Exception ex)
        {
            FileExplorerStatusText = $"reveal failed: {ex.Message}";
        }
    }

    /// <summary>Opens the tab's project root in the default file manager (Windows Explorer).</summary>
    public void OpenTabFolder(TerminalTabViewModel? tab)
    {
        var path = tab?.Project.Path;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Directory.Exists(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            StatusText = $"open folder failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Installs Drover hook entries into the project's <c>.claude/settings.local.json</c>
    /// and refreshes the per-tab status flag for every tab pointing at that path.
    /// </summary>
    public void InstallHookForProject(ProjectDefinition? project)
    {
        if (project is null || string.IsNullOrEmpty(project.Path)) return;
        var s = _settings.Current;
        if (_hooksInstaller.TryInstallProject(project.Path,
                takeOverStatusLine: s.TakeOverStatusLine,
                hooksEnabled: s.HooksEnabled,
                idleHookEnabled: s.IdleHookEnabled))
        {
            RefreshHooksStatusFor(project.Path);
            StatusText = $"hook installed in {project.Name}";
        }
        else
        {
            StatusText = $"hook install failed for {project.Name}";
        }
    }

    /// <summary>Rechecks hook-status for every distinct project across open tabs and the secondary slot.</summary>
    public void RefreshAllHooksStatus()
    {
        var seen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var t in Tabs)
            if (seen.Add(t.Project.Path)) RefreshHooksStatusFor(t.Project.Path);
        if (SecondaryTab is { } sec && seen.Add(sec.Project.Path))
            RefreshHooksStatusFor(sec.Project.Path);
    }

    /// <summary>Refreshes <see cref="TerminalTabViewModel.HooksInstalled"/> for every tab pointing at the given path.</summary>
    public void RefreshHooksStatusFor(string projectPath)
    {
        var installed = _hooksInstaller.IsInstalledForProject(projectPath);
        foreach (var t in Tabs)
            if (string.Equals(t.Project.Path, projectPath, System.StringComparison.OrdinalIgnoreCase))
                t.HooksInstalled = installed;
        if (SecondaryTab is { } sec
            && string.Equals(sec.Project.Path, projectPath, System.StringComparison.OrdinalIgnoreCase))
            sec.HooksInstalled = installed;
    }

    public RelayCommand ShowDashboardCommand { get; }
    public RelayCommand ShowTokenomicsCommand { get; }
    public RelayCommand ShowMemoryCommand { get; }
    public RelayCommand BackToSessionCommand { get; }
    public RelayCommand RefreshMemoryCommand { get; }
    public RelayCommand<MemoryFile> SelectMemoryCommand { get; }
    public RelayCommand ToggleMemoryEditCommand { get; }
    public RelayCommand SaveMemoryCommand { get; }
    public RelayCommand<TerminalTabViewModel> FocusTabCommand { get; }

    private bool _memoryScanning;
    private bool _suppressDirty;

    public void RefreshMemory()
    {
        if (_memoryScanning) return;
        _memoryScanning = true;
        // Snapshot project paths on the UI thread, scan on a worker, then marshal results back.
        var paths = _catalog.Projects.Select(p => p.Path).ToList();
        System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                var (user, proj) = MemoryScanner.Scan(paths);
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new System.Action(() =>
                {
                    UserMemoryFiles.Clear();
                    foreach (var f in user) UserMemoryFiles.Add(f);
                    ProjectMemoryFiles.Clear();
                    foreach (var f in proj) ProjectMemoryFiles.Add(f);
                    HasNoMemory = UserMemoryFiles.Count == 0 && ProjectMemoryFiles.Count == 0;

                    if (SelectedMemoryPath is null)
                    {
                        var first = user.FirstOrDefault() ?? proj.FirstOrDefault();
                        if (first is not null) SelectMemory(first);
                    }
                    _memoryScanning = false;
                }));
            }
            catch
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(new System.Action(() => _memoryScanning = false));
            }
        });
    }

    private void SelectMemory(MemoryFile? file)
    {
        if (file is null) return;
        SelectedMemoryName = file.Name;
        SelectedMemoryPath = file.Path;
        _suppressDirty = true;
        try
        {
            using var fs = new System.IO.FileStream(file.Path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            using var sr = new System.IO.StreamReader(fs);
            SelectedMemoryContent = sr.ReadToEnd();
        }
        catch (System.Exception ex)
        {
            SelectedMemoryContent = $"[unable to read file: {ex.Message}]";
        }
        finally
        {
            _suppressDirty = false;
        }
        MemoryDirty = false;
        MemorySaveStatus = string.Empty;
        MemoryEditMode = false;
        MemoryRendered = MarkdownRenderer.Render(SelectedMemoryContent);
    }

    private void SaveMemory()
    {
        if (SelectedMemoryPath is null) return;
        try
        {
            System.IO.File.WriteAllText(SelectedMemoryPath, SelectedMemoryContent);
            MemoryDirty = false;
            MemoryRendered = MarkdownRenderer.Render(SelectedMemoryContent);
            MemorySaveStatus = $"Saved {System.DateTime.Now:HH:mm:ss}";
        }
        catch (System.Exception ex)
        {
            MemorySaveStatus = $"Save failed: {ex.Message}";
        }
    }

    partial void OnMemoryEditModeChanged(bool value)
    {
        if (!value) MemoryRendered = MarkdownRenderer.Render(SelectedMemoryContent);
    }

    private void FocusTab(TerminalTabViewModel? tab)
    {
        if (tab is null) return;
        SelectedTab = tab;
        DashboardActive = false;
    }

    public SettingsStore Settings => _settings;
    public ActivityLog Activity => _activity;
    public RelayCommand ToggleActivityCommand { get; }
    public RelayCommand<TerminalTabViewModel> SplitRightCommand { get; }
    public RelayCommand CloseSecondaryCommand { get; }

    public ObservableCollection<ProjectDefinition> Projects => _catalog.Projects;
    public ObservableCollection<ProjectListItem> ProjectItems { get; } = new();
    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = new();

    private void OnCatalogChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
        {
            foreach (ProjectDefinition old in e.OldItems)
            {
                _gitStatus.Unwatch(old.Path);
                for (int i = ProjectItems.Count - 1; i >= 0; i--)
                {
                    if (ReferenceEquals(ProjectItems[i].Project, old)) { ProjectItems.RemoveAt(i); break; }
                }
            }
        }
        if (e.NewItems is not null)
        {
            // Use the catalog's index so ProjectItems mirrors order. Falls back to Add
            // when the catalog event reports an unknown action (Reset/Move).
            foreach (ProjectDefinition added in e.NewItems)
            {
                var idx = _catalog.Projects.IndexOf(added);
                var item = new ProjectListItem(added);
                if (idx >= 0 && idx <= ProjectItems.Count) ProjectItems.Insert(idx, item);
                else ProjectItems.Add(item);
                _gitStatus.Watch(added.Path);
            }
        }
        if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Replace
            && e.OldItems is { Count: > 0 } && e.NewItems is { Count: > 0 })
        {
            // Catch-all: if the catalog Replace fired both old/new, the loops above will
            // have removed-then-added — fine, but make sure we re-watch the new path even
            // if it equals the old (pure metadata edit). _gitStatus.Watch is idempotent.
            foreach (ProjectDefinition added in e.NewItems) _gitStatus.Watch(added.Path);
        }
    }

    private void OnGitStatusChanged(object? sender, (string Path, GitStatus Status) e)
    {
        var text = e.Status.ChipText;
        foreach (var item in ProjectItems)
        {
            if (string.Equals(item.Project.Path, e.Path, System.StringComparison.OrdinalIgnoreCase))
            {
                item.GitText = text;
                item.GitDirty = e.Status.Dirty;
                item.GitHasRepo = e.Status.IsRepo;
            }
        }
        foreach (var t in Tabs)
        {
            if (string.Equals(t.Project.Path, e.Path, System.StringComparison.OrdinalIgnoreCase))
            {
                t.GitText = text;
                t.GitDirty = e.Status.Dirty;
                t.GitHasRepo = e.Status.IsRepo;
            }
        }
        if (SecondaryTab is { } sec
            && string.Equals(sec.Project.Path, e.Path, System.StringComparison.OrdinalIgnoreCase))
        {
            sec.GitText = text;
            sec.GitDirty = e.Status.Dirty;
            sec.GitHasRepo = e.Status.IsRepo;
        }
    }

    public RelayCommand<ProjectDefinition> OpenProjectCommand { get; }
    public RelayCommand<TerminalTabViewModel> CloseTabCommand { get; }
    public RelayCommand<ProjectDefinition> RemoveProjectCommand { get; }

    public void AddProject(ProjectDefinition project) => _catalog.Add(project);
    public void ReplaceProject(ProjectDefinition oldProject, ProjectDefinition newProject) => _catalog.Replace(oldProject, newProject);

    private void OpenProject(ProjectDefinition? project) => OpenProjectInternal(project, dangerouslySkipPermissions: false);

    /// <summary>
    /// Always opens a new tab for <paramref name="project"/> with Claude's
    /// <c>--dangerously-skip-permissions</c> flag, bypassing the existing-tab
    /// shortcut used by <see cref="OpenProject"/>. This is an explicit user
    /// action (sidebar right-click) so we don't reuse an unscary session.
    /// </summary>
    public void OpenProjectDangerously(ProjectDefinition? project) => OpenProjectInternal(project, dangerouslySkipPermissions: true);

    private void OpenProjectInternal(ProjectDefinition? project, bool dangerouslySkipPermissions)
    {
        if (project is null) return;

        // When a dashboard/tokenomics/memory overlay is open and we're doing a
        // normal launch, treat the project click as "take me back to a session
        // for this project": prefer the first existing tab and just close the
        // overlay. Skipped for the dangerously-skip-permissions path so the
        // user always gets a fresh tab with the flag actually applied.
        var overlayOpen = DashboardActive || TokenomicsActive || MemoryActive;
        if (overlayOpen && !dangerouslySkipPermissions)
        {
            TerminalTabViewModel? match = null;
            foreach (var t in Tabs)
            {
                if (ReferenceEquals(t.Project, project) || t.Project.Name == project.Name)
                {
                    match = t;
                    break;
                }
            }
            if (match is not null)
            {
                SelectedTab = match;
                DashboardActive = false;
                TokenomicsActive = false;
                MemoryActive = false;
                return;
            }
        }

        var existing = 0;
        foreach (var t in Tabs)
            if (ReferenceEquals(t.Project, project) || t.Project.Name == project.Name) existing++;
        var title = existing == 0 ? project.Name : $"{project.Name} #{existing + 1}";
        if (dangerouslySkipPermissions) title += " ⚠";
        var s = _settings.Current;
        var tab = new TerminalTabViewModel(
            project, title,
            s.FontFamily, s.FontSize,
            resume: false,
            hooksUrl: _hooks.Url,
            dangerouslySkipPermissions: dangerouslySkipPermissions,
            claudeExecutablePath: s.ClaudeExecutablePath,
            defaultClaudeModel: s.DefaultClaudeModel,
            globalEnvVars: s.GlobalEnvVars,
            logOptions: new SessionLogOptions(s.LogKeepCount, s.LogByteBudgetMb, s.SessionLogLevel));
        _hooks.Register(tab.SessionId, evt =>
        {
            tab.OnHookEvent(evt);
            _liveTodos.Ingest(evt);
            _buildTracker.Ingest(evt, tab.Project.Path);
        });
        _hooks.RegisterStatus(tab.SessionId, tab.OnStatusLine);
        tab.StatusLineUpdated += OnTabStatusLineUpdated;
        tab.HooksInstalled = _hooksInstaller.IsInstalledForProject(project.Path);
        var cached = _gitStatus.Get(project.Path);
        tab.GitText = cached.ChipText;
        tab.GitDirty = cached.Dirty;
        tab.GitHasRepo = cached.IsRepo;
        Tabs.Add(tab);
        SelectedTab = tab;
        DashboardActive = false;
        TokenomicsActive = false;
        MemoryActive = false;
    }

    private void CloseTab(TerminalTabViewModel? tab)
    {
        if (tab is null) return;
        var idx = Tabs.IndexOf(tab);
        Tabs.Remove(tab);
        _hooks.Unregister(tab.SessionId);
        _hooks.UnregisterStatus(tab.SessionId);
        tab.StatusLineUpdated -= OnTabStatusLineUpdated;
        tab.Dispose();
        if (Tabs.Count > 0)
            SelectedTab = Tabs[System.Math.Min(idx, Tabs.Count - 1)];
    }

    private void RemoveProject(ProjectDefinition? project)
    {
        if (project is null) return;
        _catalog.Remove(project);
    }

    private void SplitRight(TerminalTabViewModel? anchor)
    {
        var project = anchor?.Project;
        if (project is null) return;
        CloseSecondary();
        var title = $"{project.Name} ▶";
        var s2 = _settings.Current;
        SecondaryTab = new TerminalTabViewModel(
            project, title,
            s2.FontFamily, s2.FontSize,
            resume: false,
            hooksUrl: _hooks.Url,
            claudeExecutablePath: s2.ClaudeExecutablePath,
            defaultClaudeModel: s2.DefaultClaudeModel,
            globalEnvVars: s2.GlobalEnvVars,
            logOptions: new SessionLogOptions(s2.LogKeepCount, s2.LogByteBudgetMb));
        _hooks.Register(SecondaryTab.SessionId, evt =>
        {
            SecondaryTab.OnHookEvent(evt);
            _liveTodos.Ingest(evt);
            _buildTracker.Ingest(evt, SecondaryTab.Project.Path);
        });
        _hooks.RegisterStatus(SecondaryTab.SessionId, SecondaryTab.OnStatusLine);
        SecondaryTab.StatusLineUpdated += OnTabStatusLineUpdated;
        SecondaryTab.HooksInstalled = _hooksInstaller.IsInstalledForProject(project.Path);
        var cachedSec = _gitStatus.Get(project.Path);
        SecondaryTab.GitText = cachedSec.ChipText;
        SecondaryTab.GitDirty = cachedSec.Dirty;
        SecondaryTab.GitHasRepo = cachedSec.IsRepo;
    }

    private void CloseSecondary()
    {
        if (SecondaryTab is not null)
        {
            _hooks.Unregister(SecondaryTab.SessionId);
            _hooks.UnregisterStatus(SecondaryTab.SessionId);
            SecondaryTab.StatusLineUpdated -= OnTabStatusLineUpdated;
        }
        SecondaryTab?.Dispose();
        SecondaryTab = null;
    }

    // ─── Plan panel ──────────────────────────────────────────────────────────

    public RelayCommand ZoomPanelsInCommand { get; private set; } = null!;
    public RelayCommand ZoomPanelsOutCommand { get; private set; } = null!;
    public RelayCommand ResetPanelZoomCommand { get; private set; } = null!;
    public RelayCommand TogglePlanPanelCommand { get; private set; } = null!;
    public RelayCommand RefreshPlanCommand { get; private set; } = null!;
    public RelayCommand<Drover.App.Models.PlanTask> SendTaskCommand { get; private set; } = null!;
    public RelayCommand<Drover.App.Models.PlanSection> SendSectionCommand { get; private set; } = null!;
    public RelayCommand ReadPlanAndCreateTasksCommand { get; private set; } = null!;
    public RelayCommand CreatePlanFromTemplateCommand { get; private set; } = null!;
    public RelayCommand OpenPlanFileCommand { get; private set; } = null!;
    public RelayCommand TogglePlanEditCommand { get; private set; } = null!;
    public RelayCommand SavePlanEditCommand { get; private set; } = null!;
    public RelayCommand CancelPlanEditCommand { get; private set; } = null!;
    public RelayCommand MarkSelectedPlanDoneCommand { get; private set; } = null!;
    public RelayCommand ReactivateSelectedPlanCommand { get; private set; } = null!;
    public RelayCommand PlanFontScaleUpCommand { get; private set; } = null!;
    public RelayCommand PlanFontScaleDownCommand { get; private set; } = null!;
    public RelayCommand PlanFontScaleResetCommand { get; private set; } = null!;

    // ─── File explorer panel commands ───────────────────────────────────────
    public RelayCommand ToggleFileExplorerPanelCommand { get; private set; } = null!;
    public RelayCommand RefreshFileExplorerCommand { get; private set; } = null!;

    // ─── Kanban panel commands ──────────────────────────────────────────────
    public RelayCommand ToggleKanbanPanelCommand { get; private set; } = null!;
    public RelayCommand ToggleSpecsPanelCommand { get; private set; } = null!;
    public RelayCommand ToggleVaultPanelCommand { get; private set; } = null!;

    // ─── Task panel commands ────────────────────────────────────────────────
    public RelayCommand ToggleTaskPanelCommand { get; private set; } = null!;
    public RelayCommand RefreshTaskListCommand { get; private set; } = null!;
    public RelayCommand<Drover.App.Models.TaskItem> SendTaskItemCommand { get; private set; } = null!;
    public RelayCommand<Drover.App.Models.TaskSection> SendTaskSectionCommand { get; private set; } = null!;
    public RelayCommand CreateTaskFileFromTemplateCommand { get; private set; } = null!;
    public RelayCommand OpenTaskFileCommand { get; private set; } = null!;
    public RelayCommand ToggleTaskEditCommand { get; private set; } = null!;
    public RelayCommand SaveTaskEditCommand { get; private set; } = null!;
    public RelayCommand CancelTaskEditCommand { get; private set; } = null!;
    public RelayCommand MarkSelectedTaskFileDoneCommand { get; private set; } = null!;
    public RelayCommand ReactivateSelectedTaskFileCommand { get; private set; } = null!;
    public RelayCommand TaskFontScaleUpCommand { get; private set; } = null!;
    public RelayCommand TaskFontScaleDownCommand { get; private set; } = null!;
    public RelayCommand BeginAddTaskCommand { get; private set; } = null!;
    public RelayCommand CommitAddTaskCommand { get; private set; } = null!;
    public RelayCommand CancelAddTaskCommand { get; private set; } = null!;
    public RelayCommand TaskFontScaleResetCommand { get; private set; } = null!;

    private TaskWatcher? _taskWatcher;
    private bool _suppressSelectedTaskFileReload;

    private PlanWatcher? _planWatcher;
    private bool _suppressSelectedPlanReload;

    public string CurrentPlansFolderRelative
        => PlanReader.ResolvePlansFolderRelative(SelectedTab?.Project.PlansFolder);

    /// <summary>
    /// Re-enumerates the available plans for the active tab and picks a sensible
    /// selection: keep the same file if it still exists, otherwise default to the
    /// first active plan, otherwise the first done plan, otherwise null.
    /// </summary>
    private void LoadPlanForSelectedTab()
    {
        _planWatcher?.Stop();

        var tab = SelectedTab;
        AvailablePlans.Clear();
        if (tab is null)
        {
            _suppressSelectedPlanReload = true;
            SelectedPlan = null;
            CurrentPlan = null;
            HasAnyPlan = false;
            _suppressSelectedPlanReload = false;
            return;
        }

        var folderRel = CurrentPlansFolderRelative;
        var entries = PlanReader.EnumeratePlans(tab.Project.Path, folderRel);
        foreach (var e in entries) AvailablePlans.Add(e);
        HasAnyPlan = AvailablePlans.Count > 0;

        var prevPath = SelectedPlan?.AbsolutePath;
        Drover.App.Models.PlanFileEntry? next = null;
        if (prevPath is not null)
            next = AvailablePlans.FirstOrDefault(e => string.Equals(e.AbsolutePath, prevPath, System.StringComparison.OrdinalIgnoreCase));
        next ??= AvailablePlans.FirstOrDefault(e => !e.IsDone) ?? AvailablePlans.FirstOrDefault();

        // Set selection (which triggers OnSelectedPlanChanged → loads the document).
        SelectedPlan = next;

        if (PlanEditMode)
        {
            PlanEditMode = false;
            PlanEditBuffer = string.Empty;
        }

        var w = new PlanWatcher();
        w.Changed += OnPlanFolderChanged;
        w.Watch(tab.Project.Path, folderRel);
        _planWatcher = w;
    }

    partial void OnSelectedPlanChanged(Drover.App.Models.PlanFileEntry? value)
    {
        if (_suppressSelectedPlanReload) return;
        SelectedPlanIsDone = value?.IsDone ?? false;
        CanMarkSelectedPlanDone = value is not null && !value.IsRoot && !value.IsDone;
        CurrentPlan = value is null ? null : PlanReader.ReadFile(value.AbsolutePath);
        if (PlanEditMode)
        {
            PlanEditMode = false;
            PlanEditBuffer = string.Empty;
        }
    }

    private void OnPlanFolderChanged(object? sender, System.EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(new System.Action(() =>
        {
            if (!PlanPanelVisible) return;
            if (PlanEditMode) return; // don't clobber user's in-progress edit
            var tab = SelectedTab;
            if (tab is null) return;

            // Re-enumerate. Selection logic mirrors LoadPlanForSelectedTab but doesn't
            // reattach the watcher (still pointed at the same folder/root).
            var folderRel = CurrentPlansFolderRelative;
            var entries = PlanReader.EnumeratePlans(tab.Project.Path, folderRel);
            AvailablePlans.Clear();
            foreach (var x in entries) AvailablePlans.Add(x);
            HasAnyPlan = AvailablePlans.Count > 0;

            var prevPath = SelectedPlan?.AbsolutePath;
            Drover.App.Models.PlanFileEntry? next = null;
            if (prevPath is not null)
                next = AvailablePlans.FirstOrDefault(p => string.Equals(p.AbsolutePath, prevPath, System.StringComparison.OrdinalIgnoreCase));
            next ??= AvailablePlans.FirstOrDefault(p => !p.IsDone) ?? AvailablePlans.FirstOrDefault();
            SelectedPlan = next;

            // If the file content changed but path stayed the same, OnSelectedPlanChanged
            // won't fire (same reference). Force a reload in that case.
            if (next is not null && ReferenceEquals(next, SelectedPlan))
                CurrentPlan = PlanReader.ReadFile(next.AbsolutePath);
        }));
    }

    private string SelectedPlanRelativeOrDefault
        => SelectedPlan?.RelativePath ?? "PLAN.md";

    // ─── File explorer ──────────────────────────────────────────────────────

    /// <summary>
    /// Rebuilds <see cref="FileExplorerRoots"/> for the currently selected tab and
    /// rebinds the recursive watcher to the project root. Called when the panel
    /// opens, the active tab changes, or the user hits Refresh / F5.
    /// </summary>
    private void LoadFileTreeForSelectedTab()
    {
        _fileExplorerWatcher?.Stop();
        FileExplorerRoots.Clear();

        var tab = SelectedTab;
        if (tab is null || string.IsNullOrEmpty(tab.Project.Path) || !System.IO.Directory.Exists(tab.Project.Path))
        {
            FileExplorerStatusText = tab is null ? "no tab selected" : "project path not found";
            return;
        }

        var root = new Drover.App.Models.FileNode(tab.Project.Path, isDirectory: true, rootPath: tab.Project.Path);
        root.IsExpanded = true; // expand the root one level by default
        FileExplorerRoots.Add(root);
        FileExplorerStatusText = tab.Project.Path;

        var w = new FileExplorerWatcher();
        w.Changed += OnFileExplorerChanged;
        w.Watch(tab.Project.Path);
        _fileExplorerWatcher = w;
    }

    private void OnFileExplorerChanged(object? sender, System.EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(new System.Action(() =>
        {
            if (!FileExplorerPanelVisible) return;
            // Reload only the directories the user has actually expanded; collapsed
            // branches drop their cache and re-enumerate when the user opens them.
            foreach (var root in FileExplorerRoots) ReloadExpanded(root);
        }));
    }

    private static void ReloadExpanded(Drover.App.Models.FileNode node)
    {
        if (!node.IsDirectory) return;
        if (node.IsExpanded)
        {
            // Snapshot current expanded descendants by relative path so we can re-expand
            // matching nodes after the reload.
            var stillOpen = new System.Collections.Generic.HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            CollectExpanded(node, stillOpen);
            node.Reload();
            ReExpand(node, stillOpen);
        }
        else
        {
            // Force the placeholder to come back so a future expand re-enumerates fresh.
            node.Reload();
        }
    }

    private static void CollectExpanded(Drover.App.Models.FileNode node, System.Collections.Generic.HashSet<string> set)
    {
        if (node.IsDirectory && node.IsExpanded)
        {
            set.Add(node.FullPath);
            foreach (var c in node.Children) CollectExpanded(c, set);
        }
    }

    private static void ReExpand(Drover.App.Models.FileNode node, System.Collections.Generic.HashSet<string> set)
    {
        if (!node.IsDirectory) return;
        if (set.Contains(node.FullPath))
        {
            node.IsExpanded = true;
            foreach (var c in node.Children) ReExpand(c, set);
        }
    }

    private void SendTask(Drover.App.Models.PlanTask? task)
    {
        if (task is null || SelectedTab is null || CurrentPlan is null) return;
        Drover.App.Models.PlanSection? owning = null;
        foreach (var s in CurrentPlan.Sections)
        {
            if (s.Tasks.Any(t => ReferenceEquals(t, task)))
            {
                owning = s;
                break;
            }
        }
        var prompt = PlanPromptBuilder.ForTask(task, owning, SelectedPlanRelativeOrDefault);
        SelectedTab.SendInput(prompt);
        var preview = task.Text.Length <= 40 ? task.Text : task.Text.Substring(0, 40) + "…";
        PlanStatusText = "Sent: " + preview;
    }

    private void SendSection(Drover.App.Models.PlanSection? section)
    {
        if (section is null || SelectedTab is null) return;
        var prompt = PlanPromptBuilder.ForSection(section, SelectedPlanRelativeOrDefault);
        SelectedTab.SendInput(prompt);
        var preview = section.Heading.Length <= 40 ? section.Heading : section.Heading.Substring(0, 40) + "…";
        PlanStatusText = "Sent section: " + preview;
    }

    private void ReadPlanAndCreateTasks()
    {
        if (SelectedTab is null) return;
        var prompt = PlanPromptBuilder.ForReadAndCreateTasks(SelectedPlanRelativeOrDefault);
        SelectedTab.SendInput(prompt);
        PlanStatusText = "Sent: Read plan and create tasks";
    }

    /// <summary>
    /// Creates a starter `PLAN.md` when no plans exist yet. Writes to the project root
    /// if the plans folder doesn't exist (bootstrap), otherwise into the folder.
    /// </summary>
    private void CreatePlanFromTemplate()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        var folderRel = CurrentPlansFolderRelative;
        var folderAbs = System.IO.Path.Combine(tab.Project.Path, folderRel);
        string targetAbs;
        if (System.IO.Directory.Exists(folderAbs))
            targetAbs = System.IO.Path.Combine(folderAbs, "PLAN.md");
        else
            targetAbs = System.IO.Path.Combine(tab.Project.Path, "PLAN.md");
        try
        {
            var dir = System.IO.Path.GetDirectoryName(targetAbs);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);
            if (!System.IO.File.Exists(targetAbs))
            {
                var content = PlanPromptBuilder.DefaultTemplate.Replace("{ProjectName}", tab.Project.Name);
                AtomicFile.WriteAllText(targetAbs, content);
                PlanStatusText = "Created " + System.IO.Path.GetFileName(targetAbs);
            }
            LoadPlanForSelectedTab();
        }
        catch (System.Exception ex)
        {
            PlanStatusText = "Create failed: " + ex.Message;
        }
    }

    private void OpenPlanFile()
    {
        if (SelectedPlan is null) { PlanStatusText = "No plan selected"; return; }
        var full = SelectedPlan.AbsolutePath;
        if (!System.IO.File.Exists(full)) { PlanStatusText = "Plan file missing"; return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full)
            {
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            PlanStatusText = "Open failed: " + ex.Message;
        }
    }

    private void TogglePlanEdit()
    {
        if (PlanEditMode)
        {
            PlanEditMode = false;
            PlanEditBuffer = string.Empty;
            return;
        }
        PlanEditBuffer = CurrentPlan?.RawMarkdown ?? string.Empty;
        PlanEditMode = true;
    }

    private void SavePlanEdit()
    {
        if (SelectedPlan is null) return;
        try
        {
            AtomicFile.WriteAllText(SelectedPlan.AbsolutePath, PlanEditBuffer);
            PlanEditMode = false;
            PlanEditBuffer = string.Empty;
            PlanStatusText = "Saved " + System.DateTime.Now.ToString("HH:mm:ss");
            // Refresh the parsed view from what we just wrote — watcher will likely fire too.
            CurrentPlan = PlanReader.ReadFile(SelectedPlan.AbsolutePath);
        }
        catch (System.Exception ex)
        {
            PlanStatusText = "Save failed: " + ex.Message;
        }
    }

    /// <summary>
    /// Moves the selected active plan into `<plansFolder>/done/`, creating the folder
    /// if needed. The watcher will pick up the move; we also explicitly refresh and
    /// re-select the moved file at its new path so the panel stays on the same plan.
    /// </summary>
    private void MarkSelectedPlanDone()
    {
        var tab = SelectedTab;
        var sel = SelectedPlan;
        if (tab is null || sel is null || sel.IsRoot || sel.IsDone) return;

        var folderRel = CurrentPlansFolderRelative;
        var folderAbs = System.IO.Path.Combine(tab.Project.Path, folderRel);
        var doneAbs = System.IO.Path.Combine(folderAbs, "done");
        try
        {
            if (!System.IO.Directory.Exists(doneAbs)) System.IO.Directory.CreateDirectory(doneAbs);
            var targetAbs = System.IO.Path.Combine(doneAbs, System.IO.Path.GetFileName(sel.AbsolutePath));
            if (System.IO.File.Exists(targetAbs))
            {
                PlanStatusText = "Done file already exists: " + System.IO.Path.GetFileName(targetAbs);
                return;
            }
            System.IO.File.Move(sel.AbsolutePath, targetAbs);
            PlanStatusText = "Moved to done/: " + sel.DisplayName;

            // Force a re-enumerate now and pick the new path.
            var entries = PlanReader.EnumeratePlans(tab.Project.Path, folderRel);
            AvailablePlans.Clear();
            foreach (var x in entries) AvailablePlans.Add(x);
            HasAnyPlan = AvailablePlans.Count > 0;
            SelectedPlan = AvailablePlans.FirstOrDefault(p => string.Equals(p.AbsolutePath, targetAbs, System.StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Exception ex)
        {
            PlanStatusText = "Mark done failed: " + ex.Message;
        }
    }

    private void ReactivateSelectedPlan()
    {
        var tab = SelectedTab;
        var sel = SelectedPlan;
        if (tab is null || sel is null || !sel.IsDone) return;

        var folderRel = CurrentPlansFolderRelative;
        var folderAbs = System.IO.Path.Combine(tab.Project.Path, folderRel);
        try
        {
            if (!System.IO.Directory.Exists(folderAbs)) System.IO.Directory.CreateDirectory(folderAbs);
            var targetAbs = System.IO.Path.Combine(folderAbs, System.IO.Path.GetFileName(sel.AbsolutePath));
            if (System.IO.File.Exists(targetAbs))
            {
                PlanStatusText = "Active file already exists: " + System.IO.Path.GetFileName(targetAbs);
                return;
            }
            System.IO.File.Move(sel.AbsolutePath, targetAbs);
            PlanStatusText = "Reactivated: " + sel.DisplayName;

            var entries = PlanReader.EnumeratePlans(tab.Project.Path, folderRel);
            AvailablePlans.Clear();
            foreach (var x in entries) AvailablePlans.Add(x);
            HasAnyPlan = AvailablePlans.Count > 0;
            SelectedPlan = AvailablePlans.FirstOrDefault(p => string.Equals(p.AbsolutePath, targetAbs, System.StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Exception ex)
        {
            PlanStatusText = "Reactivate failed: " + ex.Message;
        }
    }

    // ─── Task panel ──────────────────────────────────────────────────────────

    private void LoadTaskListForSelectedTab()
    {
        _taskWatcher?.Stop();

        var tab = SelectedTab;
        AvailableTaskFiles.Clear();
        if (tab is null)
        {
            _suppressSelectedTaskFileReload = true;
            SelectedTaskFile = null;
            CurrentTaskList = null;
            HasAnyTaskFile = false;
            _suppressSelectedTaskFileReload = false;
            return;
        }

        var entries = TaskReader.EnumerateTaskFiles(tab.Project.Path);
        foreach (var e in entries) AvailableTaskFiles.Add(e);
        HasAnyTaskFile = AvailableTaskFiles.Count > 0;

        var prevPath = SelectedTaskFile?.AbsolutePath;
        Drover.App.Models.TaskFileEntry? next = null;
        if (prevPath is not null)
            next = AvailableTaskFiles.FirstOrDefault(e => string.Equals(e.AbsolutePath, prevPath, System.StringComparison.OrdinalIgnoreCase));
        next ??= AvailableTaskFiles.FirstOrDefault(e => !e.IsDone) ?? AvailableTaskFiles.FirstOrDefault();

        SelectedTaskFile = next;

        if (TaskEditMode)
        {
            TaskEditMode = false;
            TaskEditBuffer = string.Empty;
        }

        var w = new TaskWatcher();
        w.Changed += OnTaskFolderChanged;
        w.Watch(tab.Project.Path);
        _taskWatcher = w;
    }

    partial void OnSelectedTaskFileChanged(Drover.App.Models.TaskFileEntry? value)
    {
        if (_suppressSelectedTaskFileReload) return;
        SelectedTaskFileIsDone = value?.IsDone ?? false;
        CanMarkSelectedTaskFileDone = value is not null && !value.IsRoot && !value.IsDone;
        CurrentTaskList = value is null ? null : TaskReader.ReadFile(value.AbsolutePath);
        if (TaskEditMode)
        {
            TaskEditMode = false;
            TaskEditBuffer = string.Empty;
        }
    }

    private void OnTaskFolderChanged(object? sender, System.EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(new System.Action(() =>
        {
            if (!TaskPanelVisible) return;
            if (TaskEditMode) return;
            var tab = SelectedTab;
            if (tab is null) return;

            var entries = TaskReader.EnumerateTaskFiles(tab.Project.Path);
            AvailableTaskFiles.Clear();
            foreach (var x in entries) AvailableTaskFiles.Add(x);
            HasAnyTaskFile = AvailableTaskFiles.Count > 0;

            var prevPath = SelectedTaskFile?.AbsolutePath;
            Drover.App.Models.TaskFileEntry? next = null;
            if (prevPath is not null)
                next = AvailableTaskFiles.FirstOrDefault(p => string.Equals(p.AbsolutePath, prevPath, System.StringComparison.OrdinalIgnoreCase));
            next ??= AvailableTaskFiles.FirstOrDefault(p => !p.IsDone) ?? AvailableTaskFiles.FirstOrDefault();
            SelectedTaskFile = next;

            if (next is not null && ReferenceEquals(next, SelectedTaskFile))
                CurrentTaskList = TaskReader.ReadFile(next.AbsolutePath);
        }));
    }

    private string SelectedTaskFileRelativeOrDefault
        => SelectedTaskFile?.RelativePath ?? "TASKS.md";

    private void SendTaskItem(Drover.App.Models.TaskItem? item)
    {
        if (item is null || SelectedTab is null || CurrentTaskList is null) return;
        Drover.App.Models.TaskSection? owning = null;
        foreach (var s in CurrentTaskList.Sections)
        {
            if (s.Items.Any(t => ReferenceEquals(t, item)))
            {
                owning = s;
                break;
            }
        }
        var prompt = TaskPromptBuilder.ForItem(item, owning, SelectedTaskFileRelativeOrDefault);
        SelectedTab.SendInput(prompt);
        var preview = item.Text.Length <= 40 ? item.Text : item.Text.Substring(0, 40) + "…";
        TaskListStatusText = "Sent: " + preview;
    }

    private void SendTaskSection(Drover.App.Models.TaskSection? section)
    {
        if (section is null || SelectedTab is null) return;
        var prompt = TaskPromptBuilder.ForSection(section, SelectedTaskFileRelativeOrDefault);
        SelectedTab.SendInput(prompt);
        var preview = section.Heading.Length <= 40 ? section.Heading : section.Heading.Substring(0, 40) + "…";
        TaskListStatusText = "Sent section: " + preview;
    }

    private void CreateTaskFileFromTemplate()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        var folderAbs = System.IO.Path.Combine(tab.Project.Path, TaskReader.TasksFolder);
        string targetAbs;
        if (System.IO.Directory.Exists(folderAbs))
            targetAbs = System.IO.Path.Combine(folderAbs, "TASKS.md");
        else
            targetAbs = System.IO.Path.Combine(tab.Project.Path, "TASKS.md");
        try
        {
            var dir = System.IO.Path.GetDirectoryName(targetAbs);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
                System.IO.Directory.CreateDirectory(dir);
            if (!System.IO.File.Exists(targetAbs))
            {
                var content = TaskPromptBuilder.DefaultTemplate.Replace("{ProjectName}", tab.Project.Name);
                AtomicFile.WriteAllText(targetAbs, content);
                TaskListStatusText = "Created " + System.IO.Path.GetFileName(targetAbs);
            }
            LoadTaskListForSelectedTab();
        }
        catch (System.Exception ex)
        {
            TaskListStatusText = "Create failed: " + ex.Message;
        }
    }

    private void OpenTaskFile()
    {
        if (SelectedTaskFile is null) { TaskListStatusText = "No list selected"; return; }
        var full = SelectedTaskFile.AbsolutePath;
        if (!System.IO.File.Exists(full)) { TaskListStatusText = "List file missing"; return; }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(full)
            {
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            TaskListStatusText = "Open failed: " + ex.Message;
        }
    }

    private void ToggleTaskEdit()
    {
        if (TaskEditMode)
        {
            TaskEditMode = false;
            TaskEditBuffer = string.Empty;
            return;
        }
        TaskEditBuffer = CurrentTaskList?.RawMarkdown ?? string.Empty;
        TaskEditMode = true;
    }

    private void SaveTaskEdit()
    {
        if (SelectedTaskFile is null) return;
        try
        {
            AtomicFile.WriteAllText(SelectedTaskFile.AbsolutePath, TaskEditBuffer);
            TaskEditMode = false;
            TaskEditBuffer = string.Empty;
            TaskListStatusText = "Saved " + System.DateTime.Now.ToString("HH:mm:ss");
            CurrentTaskList = TaskReader.ReadFile(SelectedTaskFile.AbsolutePath);
        }
        catch (System.Exception ex)
        {
            TaskListStatusText = "Save failed: " + ex.Message;
        }
    }

    private void BeginAddTask()
    {
        // Reveal the inline composer. Disabled at the UI level when there's no
        // selected task file or the file is archived; this is a defensive guard.
        if (SelectedTaskFile is null || SelectedTaskFile.IsDone) return;
        TaskComposerText = string.Empty;
        TaskComposerVisible = true;
    }

    private void CancelAddTask()
    {
        TaskComposerVisible = false;
        TaskComposerText = string.Empty;
    }

    private void CommitAddTask()
    {
        if (SelectedTaskFile is null || SelectedTaskFile.IsDone) return;
        var title = (TaskComposerText ?? string.Empty).Trim();
        if (title.Length == 0) { CancelAddTask(); return; }

        try
        {
            var path = SelectedTaskFile.AbsolutePath;
            var existing = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : string.Empty;
            // Append at the end of the file. If sections exist, the new line attaches
            // to the last section as parsed by TaskReader — fine for a quick-add. Users
            // who want precise placement can drop into edit mode.
            var needsLeadingNewline = existing.Length > 0 && !existing.EndsWith("\n");
            var separator = needsLeadingNewline ? System.Environment.NewLine : string.Empty;
            var addition = $"{separator}- [ ] {title}{System.Environment.NewLine}";
            AtomicFile.WriteAllText(path, existing + addition);
            CurrentTaskList = TaskReader.ReadFile(path);
            TaskListStatusText = "Added task " + System.DateTime.Now.ToString("HH:mm:ss");
        }
        catch (System.Exception ex)
        {
            TaskListStatusText = "Add failed: " + ex.Message;
        }

        TaskComposerText = string.Empty;
        TaskComposerVisible = false;
    }

    private void MarkSelectedTaskFileDone()
    {
        var tab = SelectedTab;
        var sel = SelectedTaskFile;
        if (tab is null || sel is null || sel.IsRoot || sel.IsDone) return;

        var folderAbs = System.IO.Path.Combine(tab.Project.Path, TaskReader.TasksFolder);
        var doneAbs = System.IO.Path.Combine(folderAbs, "done");
        try
        {
            if (!System.IO.Directory.Exists(doneAbs)) System.IO.Directory.CreateDirectory(doneAbs);
            var targetAbs = System.IO.Path.Combine(doneAbs, System.IO.Path.GetFileName(sel.AbsolutePath));
            if (System.IO.File.Exists(targetAbs))
            {
                TaskListStatusText = "Done file already exists: " + System.IO.Path.GetFileName(targetAbs);
                return;
            }
            System.IO.File.Move(sel.AbsolutePath, targetAbs);
            TaskListStatusText = "Moved to done/: " + sel.DisplayName;

            var entries = TaskReader.EnumerateTaskFiles(tab.Project.Path);
            AvailableTaskFiles.Clear();
            foreach (var x in entries) AvailableTaskFiles.Add(x);
            HasAnyTaskFile = AvailableTaskFiles.Count > 0;
            SelectedTaskFile = AvailableTaskFiles.FirstOrDefault(p => string.Equals(p.AbsolutePath, targetAbs, System.StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Exception ex)
        {
            TaskListStatusText = "Mark done failed: " + ex.Message;
        }
    }

    private void ReactivateSelectedTaskFile()
    {
        var tab = SelectedTab;
        var sel = SelectedTaskFile;
        if (tab is null || sel is null || !sel.IsDone) return;

        var folderAbs = System.IO.Path.Combine(tab.Project.Path, TaskReader.TasksFolder);
        try
        {
            if (!System.IO.Directory.Exists(folderAbs)) System.IO.Directory.CreateDirectory(folderAbs);
            var targetAbs = System.IO.Path.Combine(folderAbs, System.IO.Path.GetFileName(sel.AbsolutePath));
            if (System.IO.File.Exists(targetAbs))
            {
                TaskListStatusText = "Active file already exists: " + System.IO.Path.GetFileName(targetAbs);
                return;
            }
            System.IO.File.Move(sel.AbsolutePath, targetAbs);
            TaskListStatusText = "Reactivated: " + sel.DisplayName;

            var entries = TaskReader.EnumerateTaskFiles(tab.Project.Path);
            AvailableTaskFiles.Clear();
            foreach (var x in entries) AvailableTaskFiles.Add(x);
            HasAnyTaskFile = AvailableTaskFiles.Count > 0;
            SelectedTaskFile = AvailableTaskFiles.FirstOrDefault(p => string.Equals(p.AbsolutePath, targetAbs, System.StringComparison.OrdinalIgnoreCase));
        }
        catch (System.Exception ex)
        {
            TaskListStatusText = "Reactivate failed: " + ex.Message;
        }
    }
}
