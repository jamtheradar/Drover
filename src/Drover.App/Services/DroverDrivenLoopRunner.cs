using Drover.App.Models;
using System.IO;

namespace Drover.App.Services;

/// <summary>
/// The long-form loop. Drover holds the queue: send the execute-spec prompt,
/// wait for the spec frontmatter to leave <c>in-progress</c>, branch on the
/// outcome, send the next prompt (or stop). Each spec runs in the same
/// session — there's no per-spec session reset; the prompt instructs Claude
/// to clear context between specs if it needs to.
/// </summary>
public sealed class DroverDrivenLoopRunner : ILoopRunner
{
    private readonly IPromptLibrary _prompts;
    private readonly HookAgentKind _agentKind;

    /// <summary>How long to wait between specs for the session to settle
    /// before sending the next prompt. Avoids racing the session's own
    /// end-of-turn rendering.</summary>
    private static readonly System.TimeSpan InterSpecDelay = System.TimeSpan.FromMilliseconds(500);

    /// <summary>Hard ceiling on how long we'll wait for a single spec's
    /// frontmatter to leave <c>in-progress</c>. A spec hung past this is
    /// almost certainly stuck (Claude died, ConPTY detached, agent crashed);
    /// stopping the loop with an error is the right call.</summary>
    private static readonly System.TimeSpan PerSpecTimeout = System.TimeSpan.FromHours(2);

    public DroverDrivenLoopRunner(IPromptLibrary prompts,
                                  HookAgentKind agentKind = HookAgentKind.Claude)
    {
        _prompts = prompts;
        _agentKind = agentKind;
    }

    public async Task RunAsync(
        IReadOnlyList<SpecFile> specs,
        string projectRoot,
        ITerminalSession session,
        System.IProgress<LoopEvent> progress,
        CancellationToken ct)
    {
        if (specs.Count == 0) return;

        progress.Report(new LoopEvent(0, specs.Count, string.Empty, LoopEventKind.Started, null));

        for (var i = 0; i < specs.Count; i++)
        {
            if (ct.IsCancellationRequested)
            {
                progress.Report(new LoopEvent(i, specs.Count, string.Empty, LoopEventKind.Stopped, "cancelled"));
                return;
            }

            var spec = specs[i];
            progress.Report(new LoopEvent(i + 1, specs.Count, spec.Id, LoopEventKind.SpecStarted, spec.FileName));

            string prompt;
            try
            {
                prompt = _prompts.Resolve("execute-spec", projectRoot, _agentKind,
                    new Dictionary<string, string> { ["SPEC_FILENAME"] = spec.FileName });
            }
            catch (System.Exception ex)
            {
                progress.Report(new LoopEvent(i + 1, specs.Count, spec.Id, LoopEventKind.Errored, $"prompt resolve failed: {ex.Message}"));
                return;
            }

            if (!session.SendInput(prompt))
            {
                progress.Report(new LoopEvent(i + 1, specs.Count, spec.Id, LoopEventKind.Errored, "session not attached"));
                return;
            }

            // The agent's first action per the prompt is to set status: in-progress
            // before any other edits. Wait for the file to leave that state.
            SpecFile? after;
            try
            {
                after = await WaitForSpecResolutionAsync(spec.Path, PerSpecTimeout, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // User pressed Stop. Best-effort interrupt and bail.
                session.SendInterrupt();
                progress.Report(new LoopEvent(i + 1, specs.Count, spec.Id, LoopEventKind.Stopped, "cancelled mid-spec"));
                return;
            }

            if (after is null)
            {
                progress.Report(new LoopEvent(i + 1, specs.Count, spec.Id, LoopEventKind.Errored, "timed out waiting for spec status update"));
                return;
            }

            switch (after.Status)
            {
                case SpecStatus.Done:
                    progress.Report(new LoopEvent(i + 1, specs.Count, spec.Id, LoopEventKind.SpecCompleted, after.FileName));
                    break;

                case SpecStatus.Blocked when after.BlockSeverity == BlockSeverity.Trivial:
                    progress.Report(new LoopEvent(i + 1, specs.Count, spec.Id, LoopEventKind.SpecBlocked, "trivial — continuing"));
                    break;

                case SpecStatus.Blocked:
                    // Major (or missing severity — safer default is to stop).
                    progress.Report(new LoopEvent(i + 1, specs.Count, spec.Id, LoopEventKind.SpecBlocked, "major — stopping"));
                    return;

                default:
                    // Still in-progress, unknown, or unparseable frontmatter. Treat as error.
                    progress.Report(new LoopEvent(i + 1, specs.Count, spec.Id, LoopEventKind.Errored,
                        $"unexpected status '{after.RawStatus}'"));
                    return;
            }

            // Settle delay between specs. Cancellation here is clean: just
            // return without sending the next prompt.
            try { await Task.Delay(InterSpecDelay, ct).ConfigureAwait(false); }
            catch (OperationCanceledException)
            {
                progress.Report(new LoopEvent(i + 1, specs.Count, spec.Id, LoopEventKind.Stopped, "cancelled"));
                return;
            }
        }

        progress.Report(new LoopEvent(specs.Count, specs.Count, string.Empty, LoopEventKind.Stopped, "done"));
    }

    /// <summary>
    /// Waits for the spec file's frontmatter <c>status</c> to leave
    /// <c>in-progress</c>. Uses a one-shot file-system watcher scoped to the
    /// spec's directory so the polling cost is zero — we re-read the file on
    /// each event and signal completion when the status flips.
    /// </summary>
    private static async Task<SpecFile?> WaitForSpecResolutionAsync(string specPath, System.TimeSpan timeout, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(specPath);
        var name = Path.GetFileName(specPath);
        if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return null;

        var tcs = new TaskCompletionSource<SpecFile?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watcher = new FileSystemWatcher(dir, name)
        {
            NotifyFilter = NotifyFilters.LastWrite
                         | NotifyFilters.Size
                         | NotifyFilters.FileName
                         | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };

        void OnAny(object? _, FileSystemEventArgs __)
        {
            var s = SafeLoad(specPath);
            if (s is null) return;
            // We only care about transitions away from in-progress. Pending
            // / unknown counts as "not yet processed" — keep waiting.
            if (s.Status is SpecStatus.Done or SpecStatus.Blocked)
                tcs.TrySetResult(s);
        }

        watcher.Changed += OnAny;
        watcher.Created += OnAny;
        watcher.Renamed += (_, e) => OnAny(null, new FileSystemEventArgs(WatcherChangeTypes.Renamed, dir, name));

        // Race: completion, cancellation, or timeout.
        using var ctReg = ct.Register(() => tcs.TrySetCanceled(ct));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        using var timeoutReg = cts.Token.Register(() =>
        {
            // If the linked CTS fired due to the *outer* token, the OperationCanceledException
            // from tcs takes precedence and the caller sees cancellation. If it fired due to
            // the timeout (outer token still running), we want a null result.
            if (!ct.IsCancellationRequested) tcs.TrySetResult(null);
        });

        // First check: the agent might have already flipped status before
        // this method was reached (very fast spec or pre-existing state).
        var initial = SafeLoad(specPath);
        if (initial is not null && initial.Status is SpecStatus.Done or SpecStatus.Blocked)
            tcs.TrySetResult(initial);

        return await tcs.Task.ConfigureAwait(false);
    }

    private static SpecFile? SafeLoad(string path)
    {
        // The agent may be mid-write when our event fires. Tolerate transient
        // IO failures by returning null — the next file event will retry.
        try { return SpecFile.Load(path); }
        catch { return null; }
    }
}
