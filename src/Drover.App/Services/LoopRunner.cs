using Drover.App.Models;

namespace Drover.App.Services;

public enum LoopEventKind
{
    Started,
    SpecStarted,
    SpecCompleted,
    SpecBlocked,
    Stopped,
    Errored,
}

/// <summary>
/// One step in a loop run, surfaced to the panel for chrome / status text.
/// The spec file itself remains the source of truth for status — these events
/// are signals for the progress strip, not state to drive row colour from.
/// </summary>
public sealed record LoopEvent(
    int Index,
    int Total,
    string SpecId,
    LoopEventKind Kind,
    string? Message);

public interface ILoopRunner
{
    /// <summary>
    /// Run the given specs in id-ascending order against the supplied session.
    /// Honours the stop policy: trivial blocks continue, major blocks stop.
    /// Reports progress via <paramref name="progress"/>; cancellation halts at
    /// the next safe point (between specs in Drover-driven mode, by sending
    /// an interrupt in session-driven mode).
    /// </summary>
    System.Threading.Tasks.Task RunAsync(
        System.Collections.Generic.IReadOnlyList<SpecFile> specs,
        string projectRoot,
        ITerminalSession session,
        System.IProgress<LoopEvent> progress,
        System.Threading.CancellationToken ct);
}
