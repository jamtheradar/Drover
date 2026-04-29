using System.Collections.Generic;
using System.Text.RegularExpressions;
using Drover.App.Models;

namespace Drover.App.Services;

public static class TaskReader
{
    private static readonly Regex HeadingRx = new(@"^(#{2,3})\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex IgnoredHeadingRx = new(@"^(#{1}|#{4,})\s+.+?\s*$", RegexOptions.Compiled);
    private static readonly Regex ItemRx = new(@"^(\s*)([-*])\s+\[([ xX])\]\s+(.+?)\s*$", RegexOptions.Compiled);
    private static readonly Regex FenceRx = new(@"^\s*```", RegexOptions.Compiled);
    private static readonly Regex WhitespaceRx = new(@"\s+", RegexOptions.Compiled);

    public const string TasksFolder = ".claude/tasks";

    /// <summary>
    /// Discovers task list files for a project: root `TASKS.md`, then `.claude/tasks/*.md`,
    /// then `.claude/tasks/done/*.md` (marked as done). Returns an empty list if neither
    /// the root file nor the folder exists. Order: root first, then active alphabetical,
    /// then done alphabetical.
    /// </summary>
    public static System.Collections.Generic.List<TaskFileEntry> EnumerateTaskFiles(string projectPath)
    {
        var result = new System.Collections.Generic.List<TaskFileEntry>();

        var rootPath = System.IO.Path.Combine(projectPath, "TASKS.md");
        if (System.IO.File.Exists(rootPath))
        {
            result.Add(new TaskFileEntry(rootPath, "TASKS.md", "TASKS.md", IsDone: false, IsRoot: true));
        }

        var folderAbs = System.IO.Path.Combine(projectPath, TasksFolder);
        if (System.IO.Directory.Exists(folderAbs))
        {
            try
            {
                var active = System.IO.Directory.EnumerateFiles(folderAbs, "*.md", System.IO.SearchOption.TopDirectoryOnly)
                    .OrderBy(p => System.IO.Path.GetFileName(p), System.StringComparer.OrdinalIgnoreCase);
                foreach (var p in active)
                {
                    var rel = System.IO.Path.Combine(TasksFolder, System.IO.Path.GetFileName(p)).Replace('\\', '/');
                    result.Add(new TaskFileEntry(p, rel, System.IO.Path.GetFileName(p), IsDone: false, IsRoot: false));
                }
            }
            catch { }

            var doneAbs = System.IO.Path.Combine(folderAbs, "done");
            if (System.IO.Directory.Exists(doneAbs))
            {
                try
                {
                    var done = System.IO.Directory.EnumerateFiles(doneAbs, "*.md", System.IO.SearchOption.TopDirectoryOnly)
                        .OrderBy(p => System.IO.Path.GetFileName(p), System.StringComparer.OrdinalIgnoreCase);
                    foreach (var p in done)
                    {
                        var rel = System.IO.Path.Combine(TasksFolder, "done", System.IO.Path.GetFileName(p)).Replace('\\', '/');
                        result.Add(new TaskFileEntry(p, rel, System.IO.Path.GetFileName(p), IsDone: true, IsRoot: false));
                    }
                }
                catch { }
            }
        }

        return result;
    }

    public static TaskDocument ReadFile(string absolutePath)
    {
        if (!System.IO.File.Exists(absolutePath))
            return new TaskDocument(absolutePath, false, string.Empty,
                System.Array.Empty<TaskSection>(), System.Array.Empty<TaskItem>());

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
            return new TaskDocument(absolutePath, false, string.Empty,
                System.Array.Empty<TaskSection>(), System.Array.Empty<TaskItem>());
        }

        return Parse(absolutePath, raw);
    }

    public static TaskDocument Parse(string filePath, string raw)
    {
        if (raw.Length > 0 && raw[0] == '﻿') raw = raw.Substring(1);
        var normalised = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalised.Split('\n');

        var sections = new List<TaskSection>();
        var orphans = new List<TaskItem>();

        int? curHeadingLine = null;
        int curHeadingLevel = 0;
        string curHeading = string.Empty;
        var curSummary = new System.Text.StringBuilder();
        var curItems = new List<TaskItem>();
        bool sawFirstItemInSection = false;
        bool inFence = false;

        void FlushSection()
        {
            if (curHeadingLine is null) return;
            var summary = TruncateSummary(WhitespaceRx.Replace(curSummary.ToString().Trim(), " "));
            sections.Add(new TaskSection(curHeadingLine.Value, curHeadingLevel, curHeading, summary, curItems));
            curHeadingLine = null;
            curHeadingLevel = 0;
            curHeading = string.Empty;
            curSummary.Clear();
            curItems = new List<TaskItem>();
            sawFirstItemInSection = false;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            int lineNum = i + 1;

            if (FenceRx.IsMatch(line)) { inFence = !inFence; continue; }
            if (inFence) continue;

            var headingMatch = HeadingRx.Match(line);
            if (headingMatch.Success)
            {
                FlushSection();
                curHeadingLine = lineNum;
                curHeadingLevel = headingMatch.Groups[1].Value.Length;
                curHeading = headingMatch.Groups[2].Value;
                continue;
            }

            if (IgnoredHeadingRx.IsMatch(line)) continue;

            var itemMatch = ItemRx.Match(line);
            if (itemMatch.Success)
            {
                var indentRaw = itemMatch.Groups[1].Value;
                int indentLevel = ComputeIndent(indentRaw);
                bool isDone = itemMatch.Groups[3].Value.Equals("x", System.StringComparison.OrdinalIgnoreCase);
                var text = itemMatch.Groups[4].Value;
                var item = new TaskItem(lineNum, text, isDone, indentLevel);

                if (curHeadingLine is null) orphans.Add(item);
                else { curItems.Add(item); sawFirstItemInSection = true; }
                continue;
            }

            if (curHeadingLine is not null && !sawFirstItemInSection)
            {
                if (!string.IsNullOrWhiteSpace(line))
                {
                    if (curSummary.Length > 0) curSummary.Append(' ');
                    curSummary.Append(line.Trim());
                }
            }
        }

        FlushSection();
        return new TaskDocument(filePath, true, raw, sections, orphans);
    }

    private static int ComputeIndent(string indentRaw)
    {
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
