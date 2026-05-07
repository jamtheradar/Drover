using Drover.App.Models;
using Drover.App.ViewModels;

namespace Drover.App.Services;

/// <summary>
/// Projects local plan/task markdown files into kanban cards. One card per top-level checkbox
/// item (orphan or section-attached). Status maps from the parsed checkbox marker:
/// <c>[ ]</c>→Pending, <c>[/]</c>→InProgress, <c>[x]</c>→Completed.
///
/// Sub-checklist children = subsequent lines whose <c>IndentLevel &gt; parent.IndentLevel</c>
/// until indent drops back to the parent's level or below.
///
/// Watcher echoes from our own writes are filtered through <see cref="RecentWriteGuard"/>.
/// </summary>
public sealed class LocalFilesKanbanSource : IKanbanSource
{
    public event System.EventHandler? CardsChanged;

    private readonly GateRegistry _gates;
    private readonly System.Collections.Generic.List<KanbanCard> _cards = new();
    private readonly object _lock = new();
    private TerminalTabViewModel? _tab;
    private PlanWatcher? _planWatcher;
    private TaskWatcher? _taskWatcher;

    public LocalFilesKanbanSource(GateRegistry gates) => _gates = gates;

    public System.Collections.Generic.IReadOnlyList<KanbanCard> Cards
    {
        get { lock (_lock) return _cards.ToArray(); }
    }

    public bool SupportsDrag => true;

    public void Activate(TerminalTabViewModel? tab)
    {
        _tab = tab;
        if (tab is null)
        {
            AppLog.Info("Kanban.Local", "Activate called with tab=null — clearing board.");
            ReplaceCards(System.Array.Empty<KanbanCard>());
            return;
        }

        AppLog.Info("Kanban.Local", $"Activate tab='{tab.Project.Name}' Path='{tab.Project.Path}' PlansFolder='{tab.Project.PlansFolder ?? "(default)"}'.");

        var pw = new PlanWatcher();
        pw.Changed += OnFolderChanged;
        pw.Watch(tab.Project.Path, PlanReader.ResolvePlansFolderRelative(tab.Project.PlansFolder));
        _planWatcher = pw;

        var tw = new TaskWatcher();
        tw.Changed += OnFolderChanged;
        tw.Watch(tab.Project.Path);
        _taskWatcher = tw;

        Reload();
    }

    public void Deactivate()
    {
        _tab = null;
        if (_planWatcher is not null) { _planWatcher.Changed -= OnFolderChanged; _planWatcher.Stop(); _planWatcher = null; }
        if (_taskWatcher is not null) { _taskWatcher.Changed -= OnFolderChanged; _taskWatcher.Stop(); _taskWatcher = null; }
        ReplaceCards(System.Array.Empty<KanbanCard>());
    }

    public System.Threading.Tasks.Task<bool> MoveCardAsync(KanbanCard card, KanbanStatus to)
    {
        if (card.SourceRef is not LocalFileRef lf)
            return System.Threading.Tasks.Task.FromResult(false);

        // `[~]` is Drover's own marker for Planned — `LocalKanbanWriter`'s regex already
        // accepts it, and the readers map it back to KanbanStatus.Planned on the next
        // reload, so the column survives a watcher round-trip. The glyph isn't part of
        // any standards-track checkbox spec; that's fine, plain markdown viewers render
        // it as a literal character without breaking, and our writer is the only thing
        // that emits it.
        var marker = to switch
        {
            KanbanStatus.Pending => ' ',
            KanbanStatus.Planned => '~',
            KanbanStatus.InProgress => '/',
            KanbanStatus.Completed => 'x',
            _ => ' ',
        };
        return LocalKanbanWriter.SetCheckboxAsync(lf.AbsolutePath, lf.LineNumber, marker);
    }

    private void OnFolderChanged(object? sender, System.EventArgs e)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) { Reload(); return; }
        dispatcher.BeginInvoke(new System.Action(Reload));
    }

    public void Reload()
    {
        var tab = _tab;
        if (tab is null)
        {
            AppLog.Info("Kanban.Local", "Reload skipped — _tab is null.");
            ReplaceCards(System.Array.Empty<KanbanCard>());
            return;
        }

        AppLog.Info("Kanban.Local", $"Reload start. ProjectPath='{tab.Project.Path}'.");

        var built = new System.Collections.Generic.List<KanbanCard>();

        // Plans
        var plans = PlanReader.EnumeratePlans(tab.Project.Path, tab.Project.PlansFolder);
        AppLog.Info("Kanban.Local", $"PlanReader.EnumeratePlans returned {plans.Count} file(s).");
        foreach (var entry in plans)
        {
            // Plans archived in `<plansFolder>/done/` are user-flagged as finished —
            // surface them in the file explorer and elsewhere, but keep their items
            // out of the kanban so the Completed column doesn't drown in archive.
            if (entry.IsDone)
            {
                AppLog.Info("Kanban.Local", $"Plan skipped (in done/ archive): '{entry.AbsolutePath}'.");
                continue;
            }
            if (RecentWriteGuard.WasRecent(entry.AbsolutePath))
            {
                AppLog.Info("Kanban.Local", $"Plan skipped (recent self-write): '{entry.AbsolutePath}'.");
                // Don't reload self-writes; let the in-memory state stand for now.
                // A subsequent reload (genuine third-party edit) will reconcile.
                continue;
            }
            var doc = PlanReader.ReadFile(entry.AbsolutePath);
            if (!doc.Exists) { AppLog.Warn("Kanban.Local", $"Plan ReadFile returned !Exists: '{entry.AbsolutePath}'."); continue; }
            int beforePlan = built.Count;
            BuildCardsFromPlan(doc, entry.IsDone, built);
            int afterPlanItems = built.Count;
            BuildCardsFromHeadings(doc.RawMarkdown, doc.FilePath, entry.IsDone, built);
            AppLog.Info("Kanban.Local", $"Plan '{entry.AbsolutePath}' → items={afterPlanItems - beforePlan}, headings={built.Count - afterPlanItems}.");
        }

        // Tasks
        var taskFiles = TaskReader.EnumerateTaskFiles(tab.Project.Path);
        AppLog.Info("Kanban.Local", $"TaskReader.EnumerateTaskFiles returned {taskFiles.Count} file(s).");
        foreach (var entry in taskFiles)
        {
            AppLog.Info("Kanban.Local", $"Task candidate: '{entry.AbsolutePath}' (IsRoot={entry.IsRoot}, IsDone={entry.IsDone}).");
            // Same rule for tasks: anything under `.claude/tasks/done/` is archive,
            // not active kanban material.
            if (entry.IsDone)
            {
                AppLog.Info("Kanban.Local", $"Task skipped (in done/ archive): '{entry.AbsolutePath}'.");
                continue;
            }
            if (RecentWriteGuard.WasRecent(entry.AbsolutePath))
            {
                AppLog.Info("Kanban.Local", $"Task skipped (recent self-write): '{entry.AbsolutePath}'.");
                continue;
            }
            var doc = TaskReader.ReadFile(entry.AbsolutePath);
            if (!doc.Exists) { AppLog.Warn("Kanban.Local", $"Task ReadFile returned !Exists: '{entry.AbsolutePath}'."); continue; }
            int beforeTask = built.Count;
            BuildCardsFromTaskDoc(doc, entry.IsDone, built);
            int afterTaskItems = built.Count;
            BuildCardsFromHeadings(doc.RawMarkdown, doc.FilePath, entry.IsDone, built);
            AppLog.Info("Kanban.Local", $"Task '{entry.AbsolutePath}' → items={afterTaskItems - beforeTask}, headings={built.Count - afterTaskItems}, sections={doc.Sections.Count}, orphans={doc.OrphanItems.Count}.");
        }

        AppLog.Info("Kanban.Local", $"Reload done. Total cards built: {built.Count}.");
        ReplaceCards(built);
    }

    private void BuildCardsFromPlan(PlanDocument doc, bool fileIsDone, System.Collections.Generic.List<KanbanCard> sink)
    {
        // Walk every parent task (orphans + section tasks). The "parent" criterion is
        // the lowest indent we encounter in its scope; everything more-indented up to
        // the next equal/lower indent becomes a sub-checklist child.
        AppendParentTasks(doc.OrphanTasks, doc.FilePath, fileIsDone, sink);
        foreach (var sec in doc.Sections)
            AppendParentTasks(sec.Tasks, doc.FilePath, fileIsDone, sink);
    }

    private void BuildCardsFromTaskDoc(TaskDocument doc, bool fileIsDone, System.Collections.Generic.List<KanbanCard> sink)
    {
        AppendParentTasksFromItems(doc.OrphanItems, doc.FilePath, fileIsDone, sink);
        foreach (var sec in doc.Sections)
            AppendParentTasksFromItems(sec.Items, doc.FilePath, fileIsDone, sink);
    }

    private void AppendParentTasks(
        System.Collections.Generic.IReadOnlyList<PlanTask> tasks,
        string filePath,
        bool fileIsDone,
        System.Collections.Generic.List<KanbanCard> sink)
    {
        if (tasks.Count == 0) return;
        var minIndent = int.MaxValue;
        foreach (var t in tasks) if (t.IndentLevel < minIndent) minIndent = t.IndentLevel;

        for (int i = 0; i < tasks.Count; i++)
        {
            var t = tasks[i];
            if (t.IndentLevel != minIndent) continue;

            var children = new System.Collections.Generic.List<KanbanSubItem>();
            for (int j = i + 1; j < tasks.Count; j++)
            {
                var c = tasks[j];
                if (c.IndentLevel <= minIndent) break;
                children.Add(new KanbanSubItem(c.LineNumber, c.Text, c.IsDone, c.IndentLevel));
            }

            sink.Add(BuildCard(filePath, t.LineNumber, t.IndentLevel, t.Text, t.IsDone, t.IsInProgress, t.IsPlanned, fileIsDone, children));
        }
    }

    private void AppendParentTasksFromItems(
        System.Collections.Generic.IReadOnlyList<TaskItem> items,
        string filePath,
        bool fileIsDone,
        System.Collections.Generic.List<KanbanCard> sink)
    {
        if (items.Count == 0) return;
        var minIndent = int.MaxValue;
        foreach (var t in items) if (t.IndentLevel < minIndent) minIndent = t.IndentLevel;

        for (int i = 0; i < items.Count; i++)
        {
            var it = items[i];
            if (it.IndentLevel != minIndent) continue;

            var children = new System.Collections.Generic.List<KanbanSubItem>();
            for (int j = i + 1; j < items.Count; j++)
            {
                var c = items[j];
                if (c.IndentLevel <= minIndent) break;
                children.Add(new KanbanSubItem(c.LineNumber, c.Text, c.IsDone, c.IndentLevel));
            }

            sink.Add(BuildCard(filePath, it.LineNumber, it.IndentLevel, it.Text, it.IsDone, it.IsInProgress, it.IsPlanned, fileIsDone, children));
        }
    }

    private KanbanCard BuildCard(
        string filePath,
        int lineNumber,
        int indent,
        string text,
        bool isDone,
        bool isInProgress,
        bool isPlanned,
        bool fileIsDone,
        System.Collections.Generic.IReadOnlyList<KanbanSubItem> children)
    {
        // File-level "done" wins as a presentation hint but doesn't override the per-item marker.
        var status = isDone ? KanbanStatus.Completed
                   : isInProgress ? KanbanStatus.InProgress
                   : isPlanned ? KanbanStatus.Planned
                   : (fileIsDone ? KanbanStatus.Completed : KanbanStatus.Pending);
        var id = $"{filePath}#{lineNumber}";
        var lastModified = SafeLastWriteUtc(filePath);
        var badges = new KanbanBadges(LastModifiedUtc: lastModified);

        // Gate parsing: trailing `<!-- gates: a,b,c -->` on the source line.
        var declared = KanbanGateParser.Parse(text);
        var displayTitle = declared.Count == 0 ? text : KanbanGateParser.StripGatesMarker(text);
        var projectPath = _tab?.Project.Path;
        var gates = declared.Count == 0
            ? (System.Collections.Generic.IReadOnlyList<KanbanGate>)System.Array.Empty<KanbanGate>()
            : _gates.Evaluate(
                new KanbanCard(id, status, displayTitle, null, System.Array.Empty<KanbanGate>(), children,
                    new LocalFileRef(filePath, lineNumber, indent), badges),
                declared, projectPath);

        return new KanbanCard(
            id,
            status,
            displayTitle,
            null,
            gates,
            children,
            new LocalFileRef(filePath, lineNumber, indent),
            badges);
    }

    private static readonly System.Text.RegularExpressions.Regex HeadingTaskRx =
        new(@"^(#{1,6})\s+\[([ xX/~])\]\s+(.+?)\s*$",
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Captures heading-as-task patterns (<c># [ ] foo</c>, <c>### [x] bar</c>) which
    /// the standard list-item parser doesn't see. Body = lines that follow until the
    /// next heading or horizontal rule. Many real plan/task files use this convention
    /// instead of bullets — we treat them as first-class kanban cards.
    /// </summary>
    private void BuildCardsFromHeadings(string raw, string filePath, bool fileIsDone, System.Collections.Generic.List<KanbanCard> sink)
    {
        if (string.IsNullOrEmpty(raw)) return;
        var normalised = raw.Replace("\r\n", "\n").Replace("\r", "\n");
        var lines = normalised.Split('\n');

        int? curStart = null;
        string curMarker = " ";
        string curText = string.Empty;
        var bodyBuilder = new System.Text.StringBuilder();
        bool inFence = false;

        void Flush()
        {
            if (curStart is null) return;
            var body = bodyBuilder.ToString().Trim();
            sink.Add(BuildHeadingCard(filePath, curStart.Value, curText, curMarker, fileIsDone, body));
            bodyBuilder.Clear();
            curStart = null;
        }

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimStart().StartsWith("```", System.StringComparison.Ordinal)) { inFence = !inFence; if (curStart is not null) AppendBody(bodyBuilder, line); continue; }
            if (inFence) { if (curStart is not null) AppendBody(bodyBuilder, line); continue; }

            var m = HeadingTaskRx.Match(line);
            if (m.Success)
            {
                Flush();
                curStart = i + 1;
                curMarker = m.Groups[2].Value;
                curText = m.Groups[3].Value;
                continue;
            }

            // Plain heading (no marker) terminates the current task body but doesn't
            // become a task itself.
            if (line.StartsWith("#"))
            {
                Flush();
                continue;
            }

            // Horizontal rule terminates the body too.
            if (line.Trim() == "---")
            {
                Flush();
                continue;
            }

            if (curStart is not null) AppendBody(bodyBuilder, line);
        }
        Flush();
    }

    private static void AppendBody(System.Text.StringBuilder sb, string line)
    {
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(line);
    }

    private KanbanCard BuildHeadingCard(string filePath, int lineNumber, string text, string marker, bool fileIsDone, string body)
    {
        bool isDone = string.Equals(marker, "x", System.StringComparison.OrdinalIgnoreCase);
        bool isInProgress = marker == "/";
        bool isPlanned = marker == "~";
        var status = isDone ? KanbanStatus.Completed
                   : isInProgress ? KanbanStatus.InProgress
                   : isPlanned ? KanbanStatus.Planned
                   : (fileIsDone ? KanbanStatus.Completed : KanbanStatus.Pending);
        var id = $"{filePath}#h{lineNumber}";
        var lastModified = SafeLastWriteUtc(filePath);
        var badges = new KanbanBadges(LastModifiedUtc: lastModified);

        var declared = KanbanGateParser.Parse(text);
        var displayTitle = declared.Count == 0 ? text : KanbanGateParser.StripGatesMarker(text);
        var projectPath = _tab?.Project.Path;
        var gates = declared.Count == 0
            ? (System.Collections.Generic.IReadOnlyList<KanbanGate>)System.Array.Empty<KanbanGate>()
            : _gates.Evaluate(
                new KanbanCard(id, status, displayTitle, body, System.Array.Empty<KanbanGate>(), System.Array.Empty<KanbanSubItem>(),
                    new LocalFileRef(filePath, lineNumber, 0), badges),
                declared, projectPath);

        return new KanbanCard(
            id,
            status,
            displayTitle,
            string.IsNullOrWhiteSpace(body) ? null : body,
            gates,
            System.Array.Empty<KanbanSubItem>(),
            new LocalFileRef(filePath, lineNumber, 0),
            badges);
    }

    private static System.DateTime? SafeLastWriteUtc(string path)
    {
        try { return System.IO.File.GetLastWriteTimeUtc(path); }
        catch { return null; }
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
