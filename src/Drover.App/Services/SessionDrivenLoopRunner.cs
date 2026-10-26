using Drover.App.Models;
using System.Text.RegularExpressions;

namespace Drover.App.Services;

/// <summary>
/// The short-form loop. Drover sends one prompt for the whole batch; the
/// in-session agent (see <c>prompt-loop-batch.md</c>) loops itself, spawning
/// one Task subagent per spec. Drover only watches the spec files (for live
/// status changes the panel reflects) and the session output for the
/// <c>[loop:*]</c> token stream that signals progress / completion.
/// </summary>
public sealed class SessionDrivenLoopRunner : ILoopRunner
{
    /// <summary>
    /// Sentinel <see cref="LoopEvent.Message"/> emitted (with <see cref="LoopEventKind.Errored"/>)
    /// when a caller attempts session-driven mode against a Codex tab. Session-
    /// driven mode relies on Claude-only Task subagents and <c>[loop:*]</c>
    /// literal emission, so refusing is the correct behaviour; the panel
    /// surfaces this verbatim as a toast.
    /// </summary>
    public const string ClaudeOnlyMessage = "session-driven loop is Claude Code only";

    private readonly IPromptLibrary _prompts;
    private readonly HookAgentKind _agentKind;

    private static readonly Regex SpecStartToken = new(
        @"\[loop:spec-start\]\s+(\S+)", RegexOptions.Compiled);
    private static readonly Regex StoppedToken = new(
        @"\[loop:stopped\](?:\s+(.*))?", RegexOptions.Compiled);
    private static readonly Regex ErroredToken = new(
        @"\[loop:errored\](?:\s+(.*))?", RegexOptions.Compiled);
    private static readonly Regex DoneToken = new(
        @"\[loop:done\]", RegexOptions.Compiled);

    public SessionDrivenLoopRunner(IPromptLibrary prompts,
                                   HookAgentKind agentKind = HookAgentKind.Claude)
    {
        _prompts = prompts;
        _agentKind = agentKind;
    }

    public async System.Threading.Tasks.Task RunAsync(
        System.Collections.Generic.IReadOnlyList<SpecFile> specs,
        string projectRoot,
        ITerminalSession session,
        System.IProgress<LoopEvent> progress,
        System.Threading.CancellationToken ct)
    {
        if (specs.Count == 0) return;

        // Session-driven mode spawns Claude Task subagents and parses [loop:*]
        // literals from session output — both Claude-only. Refuse cleanly so
        // the caller can surface a toast; don't silently fall back to
        // Drover-driven because that's a different observable mode.
        if (_agentKind == HookAgentKind.Codex)
        {
            progress.Report(new LoopEvent(0, specs.Count, string.Empty,
                LoopEventKind.Errored, ClaudeOnlyMessage));
            return;
        }

        // Per-spec agent assignments ride along as an `@name` suffix, e.g.
        // "0015-foo.md @code-reviewer, 0016-bar.md". Kept as one readable
        // comma list rather than JSON — the coordinator prompt is markdown a
        // user reads in their terminal. See prompt-loop-batch.md.
        var filenames = string.Join(", ", specs.Select(FormatSpecEntry));
        string prompt;
        try
        {
            prompt = _prompts.Resolve("loop-batch", projectRoot,
                new System.Collections.Generic.Dictionary<string, string>
                {
                    ["SPEC_FILENAMES"] = filenames,
                });
        }
        catch (System.Exception ex)
        {
            progress.Report(new LoopEvent(0, specs.Count, string.Empty, LoopEventKind.Errored, $"prompt resolve failed: {ex.Message}"));
            return;
        }

        progress.Report(new LoopEvent(0, specs.Count, string.Empty, LoopEventKind.Started, null));

        if (!session.SendInput(prompt))
        {
            progress.Report(new LoopEvent(0, specs.Count, string.Empty, LoopEventKind.Errored, "session not attached"));
            return;
        }

        // Tail the session output for [loop:*] tokens. The agent prints them
        // on their own lines but ConPTY may chunk a line across multiple
        // events, so accumulate into a small line buffer and parse line by
        // line. Cap the buffer to avoid unbounded growth on a chatty session.
        var tcs = new System.Threading.Tasks.TaskCompletionSource<LoopEventKind>(
            System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously);
        var buffer = new System.Text.StringBuilder();
        var index = 0; // 1-based per spec when emitting SpecStarted

        void OnOutput(object? _, string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;
            lock (buffer)
            {
                buffer.Append(StripAnsi(chunk));
                if (buffer.Length > 64 * 1024)
                {
                    // Drop the oldest half — preserves any in-flight token at the tail.
                    buffer.Remove(0, buffer.Length / 2);
                }
            }
            ScanBuffer();
        }

        void ScanBuffer()
        {
            string snapshot;
            lock (buffer) { snapshot = buffer.ToString(); }

            // Walk the buffer line by line. We only consume up to the last
            // newline so a partially-received final line stays for the next
            // chunk.
            var lastNl = snapshot.LastIndexOf('\n');
            if (lastNl < 0) return;
            var consumed = snapshot.Substring(0, lastNl + 1);
            lock (buffer) { buffer.Remove(0, consumed.Length); }

            foreach (var rawLine in consumed.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r').Trim();
                if (line.Length == 0) continue;

                if (SpecStartToken.Match(line) is { Success: true } ms)
                {
                    var fileName = ms.Groups[1].Value;
                    index++;
                    var specId = SpecIdFor(specs, fileName);
                    progress.Report(new LoopEvent(index, specs.Count, specId, LoopEventKind.SpecStarted, fileName));
                    continue;
                }

                if (StoppedToken.Match(line) is { Success: true } mst)
                {
                    progress.Report(new LoopEvent(index, specs.Count, string.Empty, LoopEventKind.Stopped,
                        mst.Groups[1].Success ? mst.Groups[1].Value : null));
                    tcs.TrySetResult(LoopEventKind.Stopped);
                    return;
                }

                if (ErroredToken.Match(line) is { Success: true } mer)
                {
                    progress.Report(new LoopEvent(index, specs.Count, string.Empty, LoopEventKind.Errored,
                        mer.Groups[1].Success ? mer.Groups[1].Value : null));
                    tcs.TrySetResult(LoopEventKind.Errored);
                    return;
                }

                if (DoneToken.IsMatch(line))
                {
                    progress.Report(new LoopEvent(specs.Count, specs.Count, string.Empty, LoopEventKind.Stopped, "done"));
                    tcs.TrySetResult(LoopEventKind.Stopped);
                    return;
                }
            }
        }

        session.Output += OnOutput;
        try
        {
            using var ctReg = ct.Register(() =>
            {
                // Best-effort: tell the in-session agent to halt at next safe
                // boundary. The agent's prompt instructs it to honour this.
                try { session.SendInterrupt(); } catch { /* swallow — we're already cancelling */ }
                tcs.TrySetCanceled(ct);
            });

            try
            {
                await tcs.Task.ConfigureAwait(false);
            }
            catch (System.OperationCanceledException)
            {
                progress.Report(new LoopEvent(index, specs.Count, string.Empty, LoopEventKind.Stopped, "cancelled"));
            }
        }
        finally
        {
            session.Output -= OnOutput;
        }
    }

    /// <summary>
    /// Formats one entry for the <c>{{SPEC_FILENAMES}}</c> list: the bare
    /// filename, plus <c>" @agent"</c> when the spec assigns one. Whitespace
    /// in an agent name would break the single-token shape the coordinator
    /// prompt parses, so such a value is treated as unassigned.
    /// </summary>
    private static string FormatSpecEntry(SpecFile spec)
    {
        var agent = spec.Agent?.Trim() ?? string.Empty;
        if (agent.Length == 0 || agent.Any(char.IsWhiteSpace)) return spec.FileName;
        return $"{spec.FileName} @{agent.TrimStart('@')}";
    }

    private static string SpecIdFor(System.Collections.Generic.IReadOnlyList<SpecFile> specs, string fileName)
    {
        foreach (var s in specs)
        {
            if (string.Equals(s.FileName, fileName, System.StringComparison.OrdinalIgnoreCase))
                return s.Id;
        }
        return string.Empty;
    }

    private static readonly Regex AnsiEscape = new("\x1b\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    /// <summary>
    /// Strips CSI escape sequences from a chunk so the line scanner can match
    /// the bare <c>[loop:*]</c> tokens. ConPTY interleaves cursor-position
    /// and SGR sequences with regular text; without this the token regex
    /// silently misses matches that have a colour escape mid-string.
    /// </summary>
    private static string StripAnsi(string s)
    {
        if (s.IndexOf('\x1b') < 0) return s;
        return AnsiEscape.Replace(s, string.Empty);
    }
}
