using Drover.App.Models;

namespace Drover.App.Services;

/// <summary>
/// Gates a card declares inline. Currently parsed from a trailing
/// <c>&lt;!-- gates: a,b,c --&gt;</c> HTML comment on the card's source line.
/// </summary>
public interface IKanbanGateEvaluator
{
    string Kind { get; }
    KanbanGate Evaluate(KanbanCard card, string? projectPath);
}

public sealed class SubtasksGate : IKanbanGateEvaluator
{
    public string Kind => "subtasks";
    public KanbanGate Evaluate(KanbanCard card, string? projectPath)
    {
        if (card.SubChecklist.Count == 0)
            return new KanbanGate(Kind, true, "no subtasks declared");
        var done = 0;
        foreach (var s in card.SubChecklist) if (s.IsDone) done++;
        var total = card.SubChecklist.Count;
        return new KanbanGate(Kind, done == total, $"{done}/{total} subtasks complete");
    }
}

public sealed class ManualGate : IKanbanGateEvaluator
{
    public string Kind => "manual";
    public KanbanGate Evaluate(KanbanCard card, string? projectPath)
        => new KanbanGate(Kind, true, "manual approval");
}

public sealed class BuildGate : IKanbanGateEvaluator
{
    private readonly BuildStatusTracker _tracker;
    public BuildGate(BuildStatusTracker tracker) => _tracker = tracker;
    public string Kind => "build";
    public KanbanGate Evaluate(KanbanCard card, string? projectPath)
    {
        if (string.IsNullOrEmpty(projectPath))
            return new KanbanGate(Kind, false, "project path unknown");
        var outcome = _tracker.GetLastFor(projectPath!);
        if (outcome is null)
            return new KanbanGate(Kind, false, "no build observed");
        var ok = outcome.ExitCode == 0;
        var age = System.DateTime.UtcNow - outcome.AtUtc;
        var ageText = age.TotalMinutes < 1 ? "just now"
                    : age.TotalHours < 1 ? $"{(int)age.TotalMinutes}m ago"
                    : $"{(int)age.TotalHours}h ago";
        return new KanbanGate(Kind, ok, ok ? $"green ({ageText})" : $"failed ({ageText})");
    }
}

/// <summary>
/// Evaluates declared gates against a card. Unknown gate kinds resolve to a failed
/// "unknown" badge so the user notices typos.
/// </summary>
public sealed class GateRegistry
{
    private readonly System.Collections.Generic.Dictionary<string, IKanbanGateEvaluator> _byKind;

    public GateRegistry(BuildStatusTracker buildTracker)
    {
        _byKind = new System.Collections.Generic.Dictionary<string, IKanbanGateEvaluator>(System.StringComparer.OrdinalIgnoreCase)
        {
            ["subtasks"] = new SubtasksGate(),
            ["manual"]   = new ManualGate(),
            ["build"]    = new BuildGate(buildTracker),
        };
    }

    public System.Collections.Generic.IReadOnlyList<KanbanGate> Evaluate(
        KanbanCard card,
        System.Collections.Generic.IReadOnlyList<string> declaredKinds,
        string? projectPath)
    {
        var result = new System.Collections.Generic.List<KanbanGate>(declaredKinds.Count);
        foreach (var kind in declaredKinds)
        {
            if (_byKind.TryGetValue(kind, out var ev))
                result.Add(ev.Evaluate(card, projectPath));
            else
                result.Add(new KanbanGate(kind, false, "unknown gate"));
        }
        return result;
    }
}
