using System.Collections.Generic;
using System.IO;
using Drover.App.Models;

namespace Drover.App.Services;

/// <summary>
/// Reads a project's plan/tasks/specs layout off disk in one pass and reports
/// both what was found and where it looked. The "where it looked" half is the
/// point: every stage of the pipeline is folder-convention driven, and the
/// usual failure is a plan sitting somewhere Drover doesn't scan.
///
/// Stateless by design — see the CLAUDE.md principle that the markdown is the
/// source of truth. Callers re-scan rather than cache.
/// </summary>
public static class WorkflowScanner
{
    public const string SpecsFolder = "specs";
    public const string DroverFolder = ".drover";

    public static WorkflowStatus Scan(string projectPath, string? plansFolderRelative, ITemplateService templates)
    {
        if (string.IsNullOrEmpty(projectPath) || !Directory.Exists(projectPath))
            return WorkflowStatus.Empty(projectPath ?? string.Empty);

        var plansFolderRel = PlanReader.ResolvePlansFolderRelative(plansFolderRelative);

        var planPaths = new List<WorkflowSearchPath>
        {
            Probe(projectPath, "PLAN.md", "Root plan — listed first, never marked done.", isFile: true),
            Probe(projectPath, plansFolderRel, "Active plans. This is the default location.", isFile: false),
            Probe(projectPath, CombineRel(plansFolderRel, "done"), "Completed plans (archive).", isFile: false),
        };
        var plans = PlanReader.EnumeratePlans(projectPath, plansFolderRel);

        var taskPaths = new List<WorkflowSearchPath>
        {
            Probe(projectPath, "TASKS.md", "Root task list.", isFile: true),
            Probe(projectPath, TaskReader.TasksFolder, "Additional task lists.", isFile: false),
            Probe(projectPath, CombineRel(TaskReader.TasksFolder, "done"), "Completed task lists (archive).", isFile: false),
        };
        var taskFiles = TaskReader.EnumerateTaskFiles(projectPath);

        int openItems = 0, totalItems = 0;
        foreach (var f in taskFiles)
        {
            if (f.IsDone) continue;
            var doc = TaskReader.ReadFile(f.AbsolutePath);
            if (!doc.Exists) continue;
            foreach (var s in doc.Sections)
                foreach (var i in s.Items) { totalItems++; if (!i.IsDone) openItems++; }
            foreach (var i in doc.OrphanItems) { totalItems++; if (!i.IsDone) openItems++; }
        }

        var specPaths = new List<WorkflowSearchPath>
        {
            Probe(projectPath, SpecsFolder, "Specs, one file per unit of work: specs/NNNN-slug.md.", isFile: false),
            Probe(projectPath, DroverFolder, "Spec template and editable prompt overrides.", isFile: false),
        };

        var specsDir = Path.Combine(projectPath, SpecsFolder);
        IReadOnlyList<SpecFile> specs = Directory.Exists(specsDir)
            ? SpecFile.Discover(specsDir)
            : System.Array.Empty<SpecFile>();

        int pending = 0, inProgress = 0, blocked = 0, done = 0;
        foreach (var s in specs)
        {
            switch (s.Status)
            {
                case SpecStatus.Pending: pending++; break;
                case SpecStatus.InProgress: inProgress++; break;
                case SpecStatus.Blocked: blocked++; break;
                case SpecStatus.Done: done++; break;
            }
        }

        bool initialized;
        try { initialized = templates.IsInitialized(projectPath); }
        catch { initialized = false; }

        return new WorkflowStatus(
            projectPath,
            plansFolderRel,
            planPaths, plans,
            taskPaths, taskFiles, openItems, totalItems,
            specPaths, specs, pending, inProgress, blocked, done,
            initialized);
    }

    private static string CombineRel(string a, string b) => (a.TrimEnd('/', '\\') + "/" + b).Replace('\\', '/');

    private static WorkflowSearchPath Probe(string root, string relative, string note, bool isFile)
    {
        var abs = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        bool exists;
        try { exists = isFile ? File.Exists(abs) : Directory.Exists(abs); }
        catch { exists = false; }
        return new WorkflowSearchPath(relative.Replace('\\', '/'), exists, note);
    }
}
