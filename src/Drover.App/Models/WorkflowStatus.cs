namespace Drover.App.Models;

/// <summary>
/// One place Drover looked for a workflow artefact, plus whether anything was
/// there. Surfaced behind the workflow panel's info icons so "no plans found"
/// is always accompanied by the paths that were searched — the empty state is
/// almost always a folder-in-the-wrong-place problem, not a missing feature.
/// </summary>
public sealed record WorkflowSearchPath(string RelativePath, bool Exists, string Note);

/// <summary>
/// Snapshot of a project's plan → tasks → specs → loop pipeline. Purely
/// derived from disk by <see cref="Drover.App.Services.WorkflowScanner"/>;
/// nothing here is cached across a file change.
/// </summary>
public sealed record WorkflowStatus(
    string ProjectPath,
    string PlansFolderRelative,
    System.Collections.Generic.IReadOnlyList<WorkflowSearchPath> PlanSearchPaths,
    System.Collections.Generic.IReadOnlyList<PlanFileEntry> Plans,
    System.Collections.Generic.IReadOnlyList<WorkflowSearchPath> TaskSearchPaths,
    System.Collections.Generic.IReadOnlyList<TaskFileEntry> TaskFiles,
    int OpenTaskItems,
    int TotalTaskItems,
    System.Collections.Generic.IReadOnlyList<WorkflowSearchPath> SpecSearchPaths,
    System.Collections.Generic.IReadOnlyList<SpecFile> Specs,
    int SpecsPending,
    int SpecsInProgress,
    int SpecsBlocked,
    int SpecsDone,
    bool DroverInitialized)
{
    public int PlanCount => Plans.Count;
    public int ActivePlanCount
    {
        get
        {
            int n = 0;
            foreach (var p in Plans) if (!p.IsDone) n++;
            return n;
        }
    }
    public int TaskFileCount => TaskFiles.Count;
    public int SpecCount => Specs.Count;
    public int RunnableSpecs => SpecsPending + SpecsInProgress;

    public static WorkflowStatus Empty(string projectPath) => new(
        projectPath,
        Services.PlanReader.DefaultPlansFolder,
        System.Array.Empty<WorkflowSearchPath>(),
        System.Array.Empty<PlanFileEntry>(),
        System.Array.Empty<WorkflowSearchPath>(),
        System.Array.Empty<TaskFileEntry>(),
        0, 0,
        System.Array.Empty<WorkflowSearchPath>(),
        System.Array.Empty<SpecFile>(),
        0, 0, 0, 0,
        false);
}
