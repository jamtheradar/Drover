using System.Collections.Generic;
using System.Text.RegularExpressions;
using Drover.App.Models;

namespace Drover.App.Services;

public static class PlanReader
{
    private static readonly Regex HeadingRx = new(@"^(#{2,3})\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex IgnoredHeadingRx = new(@"^(#{1}|#{4,})\s+.+?\s*$", RegexOptions.Compiled);
    private static readonly Regex TaskRx = new(@"^(\s*)([-*])\s+\[([ xX])\]\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex FenceRx = new(@"^\s*```", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled);

    public const string DefaultPlansFolder = ".claude/plans";

    /// <summary>
    /// Resolves the per-project plans folder, defaulting to `.claude/plans/` when unset.
    /// Returns the relative form (with backslashes normalised to forward slashes for display).
    /// </summary>
    public static string ResolvePlansFolderRelative(string? configured)
        => string.IsNullOrWhiteSpace(configured) ? DefaultPlansFolder : configured!;

    /// <summary>
    /// Discovers plan files for a project: root `PLAN.md`, then `<plansFolder>/*.md`,
    /// then `<plansFolder>/done/*.md` (marked as done). Returns an empty list if neither
    /// the root file nor the folder exists. Order: root first, then active plans
    /// alphabetical, then done plans alphabetical.
    /// </summary>
    public static System.Collections.Generic.List<PlanFileEntry> EnumeratePlans(string projectPath, string? plansFolderRelative)
    {
        var result = new System.Collections.Generic.List<PlanFileEntry>();
        var folderRel = ResolvePlansFolderRelative(plansFolderRelative);

        // 1. Root PLAN.md (if it exists). Always listed first, never marked done.
        var rootPath = System.IO.Path.Combine(projectPath, "PLAN.md");
        if (System.IO.File.Exists(rootPath))
        {
            result.Add(new PlanFileEntry(rootPath, "PLAN.md", "PLAN.md", IsDone: false, IsRoot: true));
        }

        // 2. <plansFolder>/*.md — active.
        var folderAbs = System.IO.Path.Combine(projectPath, folderRel);
        if (System.IO.Directory.Exists(folderAbs))
        {
            try
            {
                var active = System.IO.Directory.EnumerateFiles(folderAbs, "*.md", System.IO.SearchOption.TopDirectoryOnly)
                    .OrderBy(p => System.IO.Path.GetFileName(p), System.StringComparer.OrdinalIgnoreCase);
                foreach (var p in active)
                {
                    var rel = System.IO.Path.Combine(folderRel, System.IO.Path.GetFileName(p)).Replace('\\', '/');
                    result.Add(new PlanFileEntry(p, rel, System.IO.Path.GetFileName(p), IsDone: false, IsRoot: false));
                }
            }
            catch { /* fallthrough — return whatever we collected */ }

            // 3. <plansFolder>/done/*.md — done.
            var doneAbs = System.IO.Path.Combine(folderAbs, "done");
            if (System.IO.Directory.Exists(doneAbs))
            {
                try
                {
                    var done = System.IO.Directory.EnumerateFiles(doneAbs, "*.md", System.IO.SearchOption.TopDirectoryOnly)
                        .OrderBy(p => System.IO.Path.GetFileName(p), System.StringComparer.OrdinalIgnoreCase);
                    foreach (var p in done)
                    {
                        var rel = System.IO.Path.Combine(folderRel, "done", System.IO.Path.GetFileName(p)).Replace('\\', '/');
                        result.Add(new PlanFileEntry(p, rel, System.IO.Path.GetFileName(p), IsDone: true, IsRoot: false));
                    }
                }
                catch { }
            }
        }

        return result;
    }

    /// <summary>
    /// Reads a plan from an absolute path. Returns a PlanDocument with Exists=false
    /// when the file is missing or unreadable — the caller can use this for empty-state UI.
    /// </summary>
    public static PlanDocument ReadFile(string absolutePath)
    {
        if (!System.IO.File.Exists(absolutePath))
            return new PlanDocument(absolutePath, false, string.Empty,
                System.Array.Empty<PlanSection>(), System.Array.Empty<PlanTask>());

        string raw;
        try
        {
            using var fs = new System.IO.FileStream(absolutePath, System.IO.FileMode.Open,
                System.IO.FileAccess.Read,
                System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            using var sr = new System.IO.StreamReader(fs);
            raw = sr.ReadToEnd();
        }
        catch
        {
            return new PlanDocument(absolutePath, false, string.Empty,
                System.Array.Empty<PlanSection>(), System.Array.Empty<PlanTask>());
        }

        return Parse(absolutePath, raw);
    }

    public static PlanDocument Parse(string filePath, string raw)
    {
        // BOM strip + line ending normalisation.
        if (raw.Length > 0 && raw[0] == '﻿') raw = raw.Substring(1);
        var normalised = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalised.Split('\n');

        var sections = new List<PlanSection>();
        var orphanTasks = new List<PlanTask>();

        // Mutable state for the current section being assembled.
        int? curHeadingLine = null;
        int curHeadingLevel = 0;
        string curHeading = string.Empty;
        var curSummary = new System.Text.StringBuilder();
        var curTasks = new List<PlanTask>();
        bool sawFirstTaskInSection = false;
        bool inFence = false;

        void FlushSection()
        {
            if (curHeadingLine is null) return;
            var summary = TruncateSummary(WhitespaceRx.Replace(curSummary.ToString().Trim(), " "));
            sections.Add(new PlanSection(curHeadingLine.Value, curHeadingLevel, curHeading, summary, curTasks));
            curHeadingLine = null;
            curHeadingLevel = 0;
            curHeading = string.Empty;
            curSummary.Clear();
            curTasks = new List<PlanTask>();
            sawFirstTaskInSection = false;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            int lineNum = i + 1;

            if (FenceRx.IsMatch(line))
            {
                inFence = !inFence;
                // Treat fence lines as content for summary purposes only if before tasks. Skip otherwise.
                continue;
            }

            if (inFence)
            {
                continue;
            }

            var headingMatch = HeadingRx.Match(line);
            if (headingMatch.Success)
            {
                FlushSection();
                curHeadingLine = lineNum;
                curHeadingLevel = headingMatch.Groups[1].Value.Length;
                curHeading = headingMatch.Groups[2].Value;
                continue;
            }

            // Ignore level-1 and level-4+ headings as content (don't capture in summary).
            if (IgnoredHeadingRx.IsMatch(line))
            {
                continue;
            }

            var taskMatch = TaskRx.Match(line);
            if (taskMatch.Success)
            {
                var indentRaw = taskMatch.Groups[1].Value;
                int indentLevel = ComputeIndent(indentRaw);
                bool isDone = taskMatch.Groups[3].Value.Equals("x", System.StringComparison.OrdinalIgnoreCase);
                var text = taskMatch.Groups[4].Value;
                var task = new PlanTask(lineNum, text, isDone, indentLevel);

                if (curHeadingLine is null)
                {
                    orphanTasks.Add(task);
                }
                else
                {
                    curTasks.Add(task);
                    sawFirstTaskInSection = true;
                }
                continue;
            }

            // Plain content. Goes into the running summary buffer if we're inside a section
            // and haven't yet seen a task. Empty/whitespace lines are fine — collapsed later.
            if (curHeadingLine is not null && !sawFirstTaskInSection)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (curSummary.Length > 0) curSummary.Append(' ');
                    curSummary.Append(line.Trim());
                }
            }
        }

        FlushSection();

        return new PlanDocument(filePath, true, raw, sections, orphanTasks);
    }

    private static int ComputeIndent(string indentRaw)
    {
        // Count tabs as 4 spaces, 2 spaces per indent level.
        int spaces = 0;
        foreach (var c in indentRaw)
        {
            if (c == '\t') spaces += 4;
            else if (c == ' ') spaces += 1;
        }
        return spaces / 2;
    }

    private static string TruncateSummary(string s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        return s.Length <= 200 ? s : s.Substring(0, 200).TrimEnd() + "…";
    }
}
