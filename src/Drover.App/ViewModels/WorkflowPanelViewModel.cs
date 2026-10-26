using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Drover.App.Models;
using Drover.App.Services;
using System.Collections.ObjectModel;
using System.Linq;

namespace Drover.App.ViewModels;

/// <summary>
/// Backing state for <see cref="Drover.App.Views.WorkflowPanel"/> — the
/// "where am I in the pipeline, and what do I do next" surface.
///
/// The panel exists because plan → tasks → spec → loop is entirely
/// folder-convention driven: when a plan lives somewhere Drover doesn't scan,
/// every downstream panel just looks empty with no explanation. So each stage
/// here reports both its contents and the exact paths that were searched, and
/// offers only the action that makes sense for the state it's in.
///
/// This VM owns no state beyond the last scan: everything is re-derived from
/// disk by <see cref="WorkflowScanner"/> on <see cref="Refresh"/>. Panel
/// navigation and file creation are delegated back to the shell through the
/// <c>Show*</c> / <c>Create*</c> callbacks rather than by holding a reference
/// to <see cref="ShellViewModel"/>, which would be circular.
/// </summary>
public sealed partial class WorkflowPanelViewModel : ObservableObject
{
    private readonly IPromptLibrary _prompts;
    private readonly ITemplateService _templates;

    private TerminalTabViewModel? _tab;

    [ObservableProperty] private bool _hasProject;
    [ObservableProperty] private string _projectName = string.Empty;
    [ObservableProperty] private string _statusText = string.Empty;

    // Stage 1 — Plan.
    [ObservableProperty] private bool _hasPlans;
    [ObservableProperty] private string _planSummary = string.Empty;
    [ObservableProperty] private string _plansFolderText = PlanReader.DefaultPlansFolder;
    [ObservableProperty] private bool _isPlanInfoOpen;
    public ObservableCollection<WorkflowSearchPath> PlanSearchPaths { get; } = new();
    public ObservableCollection<PlanFileEntry> Plans { get; } = new();
    [ObservableProperty] private PlanFileEntry? _selectedPlan;

    // Stage 2 — Tasks.
    [ObservableProperty] private bool _hasTaskFiles;
    [ObservableProperty] private string _taskSummary = string.Empty;
    [ObservableProperty] private bool _isTaskInfoOpen;
    public ObservableCollection<WorkflowSearchPath> TaskSearchPaths { get; } = new();

    // Stage 3 — Specs.
    [ObservableProperty] private bool _hasSpecs;
    [ObservableProperty] private bool _isDroverInitialized;
    [ObservableProperty] private string _specSummary = string.Empty;
    [ObservableProperty] private bool _isSpecInfoOpen;
    public ObservableCollection<WorkflowSearchPath> SpecSearchPaths { get; } = new();

    // Stage 4 — Run loop.
    [ObservableProperty] private bool _canRunLoop;
    [ObservableProperty] private string _loopSummary = string.Empty;

    /// <summary>Set by the shell — opens the corresponding right-pane panel.</summary>
    public System.Action? ShowPlanPanel { get; set; }
    public System.Action? ShowTaskPanel { get; set; }
    public System.Action? ShowSpecsPanel { get; set; }

    /// <summary>Set by the shell — reuses its existing template-writing commands
    /// so starter PLAN.md / TASKS.md creation lives in exactly one place.</summary>
    public System.Action? CreatePlanFile { get; set; }
    public System.Action? CreateTaskFile { get; set; }
    public System.Action? InitializeSpecs { get; set; }

    public IRelayCommand RefreshCommand { get; }
    public IRelayCommand CreatePlanCommand { get; }
    public IRelayCommand OpenPlanPanelCommand { get; }
    public IRelayCommand GenerateTasksFromPlanCommand { get; }
    public IRelayCommand CreateTasksFileCommand { get; }
    public IRelayCommand OpenTaskPanelCommand { get; }
    public IRelayCommand InitializeSpecsCommand { get; }
    public IRelayCommand OpenSpecsPanelCommand { get; }
    public IRelayCommand OpenProjectFolderCommand { get; }
    public IRelayCommand TogglePlanInfoCommand { get; }
    public IRelayCommand ToggleTaskInfoCommand { get; }
    public IRelayCommand ToggleSpecInfoCommand { get; }

    public WorkflowPanelViewModel(IPromptLibrary prompts, ITemplateService templates)
    {
        _prompts = prompts;
        _templates = templates;

        RefreshCommand = new RelayCommand(Refresh);
        CreatePlanCommand = new RelayCommand(() => { CreatePlanFile?.Invoke(); Refresh(); }, () => _tab is not null);
        OpenPlanPanelCommand = new RelayCommand(() => ShowPlanPanel?.Invoke());
        GenerateTasksFromPlanCommand = new RelayCommand(GenerateTasksFromPlan, () => _tab is not null && SelectedPlan is not null);
        CreateTasksFileCommand = new RelayCommand(() => { CreateTaskFile?.Invoke(); Refresh(); }, () => _tab is not null);
        OpenTaskPanelCommand = new RelayCommand(() => ShowTaskPanel?.Invoke());
        InitializeSpecsCommand = new RelayCommand(() => { InitializeSpecs?.Invoke(); Refresh(); }, () => _tab is not null);
        OpenSpecsPanelCommand = new RelayCommand(() => ShowSpecsPanel?.Invoke());
        OpenProjectFolderCommand = new RelayCommand(OpenProjectFolder, () => _tab is not null);
        TogglePlanInfoCommand = new RelayCommand(() => IsPlanInfoOpen = !IsPlanInfoOpen);
        ToggleTaskInfoCommand = new RelayCommand(() => IsTaskInfoOpen = !IsTaskInfoOpen);
        ToggleSpecInfoCommand = new RelayCommand(() => IsSpecInfoOpen = !IsSpecInfoOpen);
    }

    public void OnTabChanged(TerminalTabViewModel? tab)
    {
        _tab = tab;
        HasProject = tab is not null;
        ProjectName = tab?.Project.Name ?? string.Empty;
        StatusText = string.Empty;
        Refresh();
    }

    /// <summary>Re-scans the project from disk. Cheap enough to call on every
    /// panel show and tab switch — no watcher, because this panel is a
    /// launchpad rather than a live view.</summary>
    public void Refresh()
    {
        if (_tab is null)
        {
            Plans.Clear();
            PlanSearchPaths.Clear();
            TaskSearchPaths.Clear();
            SpecSearchPaths.Clear();
            HasPlans = false;
            HasTaskFiles = false;
            HasSpecs = false;
            IsDroverInitialized = false;
            CanRunLoop = false;
            PlanSummary = "Open a project tab to see its workflow.";
            TaskSummary = string.Empty;
            SpecSummary = string.Empty;
            LoopSummary = string.Empty;
            RaiseCommandStates();
            return;
        }

        var status = WorkflowScanner.Scan(_tab.Project.Path, _tab.Project.PlansFolder, _templates);

        PlansFolderText = status.PlansFolderRelative;

        PlanSearchPaths.Clear();
        foreach (var p in status.PlanSearchPaths) PlanSearchPaths.Add(p);
        TaskSearchPaths.Clear();
        foreach (var p in status.TaskSearchPaths) TaskSearchPaths.Add(p);
        SpecSearchPaths.Clear();
        foreach (var p in status.SpecSearchPaths) SpecSearchPaths.Add(p);

        var previous = SelectedPlan?.AbsolutePath;
        Plans.Clear();
        foreach (var p in status.Plans) Plans.Add(p);
        HasPlans = Plans.Count > 0;
        SelectedPlan = Plans.FirstOrDefault(p => string.Equals(p.AbsolutePath, previous, System.StringComparison.OrdinalIgnoreCase))
                       ?? Plans.FirstOrDefault(p => !p.IsDone)
                       ?? Plans.FirstOrDefault();

        PlanSummary = status.PlanCount == 0
            ? $"No plans found — nothing in {status.PlansFolderRelative}/ and no PLAN.md."
            : $"{status.PlanCount} plan{Plural(status.PlanCount)} found · {status.ActivePlanCount} active.";

        HasTaskFiles = status.TaskFileCount > 0;
        TaskSummary = status.TaskFileCount == 0
            ? "No task lists found — no TASKS.md and no .claude/tasks/."
            : $"{status.TaskFileCount} list{Plural(status.TaskFileCount)} · {status.OpenTaskItems} open of {status.TotalTaskItems} item{Plural(status.TotalTaskItems)}.";

        IsDroverInitialized = status.DroverInitialized;
        HasSpecs = status.SpecCount > 0;
        SpecSummary = status.SpecCount == 0
            ? (status.DroverInitialized
                ? "No specs yet in specs/ — promote a task to write the first one."
                : "Not initialized — .drover/ (spec template + editable prompts) doesn't exist yet.")
            : $"{status.SpecCount} spec{Plural(status.SpecCount)} · {status.SpecsPending} pending, {status.SpecsInProgress} in-progress, {status.SpecsBlocked} blocked, {status.SpecsDone} done.";

        CanRunLoop = status.RunnableSpecs > 0;
        LoopSummary = status.SpecCount == 0
            ? "Nothing to run — the loop executes specs, so a spec has to exist first."
            : (status.RunnableSpecs > 0
                ? $"{status.RunnableSpecs} spec{Plural(status.RunnableSpecs)} ready to run."
                : "All specs are done or blocked — nothing pending to run.");

        RaiseCommandStates();
    }

    /// <summary>
    /// Sends the <c>plan-to-tasks</c> prompt for the selected plan. This is the
    /// step that was previously undiscoverable: the plan panel's "read plan and
    /// create tasks" only tidies checkboxes <em>inside</em> the plan, so nothing
    /// ever produced the TASKS.md that spec promotion reads from.
    /// </summary>
    private void GenerateTasksFromPlan()
    {
        var tab = _tab;
        var plan = SelectedPlan;
        if (tab is null || plan is null) return;

        var tasksRel = TaskFileRelativeForPrompt(tab.Project.Path);
        string prompt;
        try
        {
            prompt = _prompts.Resolve(
                "plan-to-tasks",
                tab.Project.Path,
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["PLAN_FILE"] = plan.RelativePath,
                    ["TASKS_FILE"] = tasksRel,
                });
        }
        catch (System.Exception ex)
        {
            StatusText = $"Plan-to-tasks prompt failed: {ex.Message}";
            return;
        }

        tab.SendInput(prompt);
        StatusText = $"Sent: derive tasks from {plan.DisplayName} into {tasksRel}.";
    }

    /// <summary>Existing root TASKS.md wins; otherwise the first active list;
    /// otherwise the conventional root path, which the prompt will create.</summary>
    private static string TaskFileRelativeForPrompt(string projectPath)
    {
        var files = TaskReader.EnumerateTaskFiles(projectPath);
        var root = files.FirstOrDefault(f => f.IsRoot);
        if (root is not null) return root.RelativePath;
        var active = files.FirstOrDefault(f => !f.IsDone);
        return active?.RelativePath ?? "TASKS.md";
    }

    private void OpenProjectFolder()
    {
        var path = _tab?.Project.Path;
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{path}\"")
            {
                UseShellExecute = true,
            });
        }
        catch (System.Exception ex)
        {
            StatusText = $"Open folder failed: {ex.Message}";
        }
    }

    private static string Plural(int n) => n == 1 ? string.Empty : "s";

    partial void OnSelectedPlanChanged(PlanFileEntry? value) => RaiseCommandStates();

    private void RaiseCommandStates()
    {
        ((RelayCommand)CreatePlanCommand).NotifyCanExecuteChanged();
        ((RelayCommand)GenerateTasksFromPlanCommand).NotifyCanExecuteChanged();
        ((RelayCommand)CreateTasksFileCommand).NotifyCanExecuteChanged();
        ((RelayCommand)InitializeSpecsCommand).NotifyCanExecuteChanged();
        ((RelayCommand)OpenProjectFolderCommand).NotifyCanExecuteChanged();
    }
}
