using System;
using System.Text;
using System.Text.RegularExpressions;
using Drover.App.Terminal;

namespace Drover.App.Services;

/// <summary>
/// Subscribes to a terminal's raw output stream and derives an AttentionState
/// by watching OSC 0/2 (window title) sequences emitted by Claude Code.
/// </summary>
public sealed class AttentionMonitor : IAttentionSource
{
    private const char Esc = '';
    private const char Bel = '';

    private const string BrailleSpinnerChars = "⠀⠁⠂⠃⠄⠆⠇⠌⠐⠠⡀⢀";

    // ESC ] (0|2) ; <title> (BEL | ESC \)
    private static readonly Regex OscTitle = new(
        @"\][02];([^]*)(?:|\\)",
        RegexOptions.Compiled);

    private readonly DroverTerminal _control;
    private readonly StringBuilder _buffer = new();
    private ConPtyConnection? _connection;
    private ConPtyConnection.InterceptDelegate? _previousIntercept;
    private ConPtyConnection.InterceptDelegate? _ourIntercept;
    private volatile bool _detached;

    public AttentionMonitor(DroverTerminal control)
    {
        _control = control;
    }

    public event EventHandler<AttentionState>? StateChanged;

    public AttentionState State { get; private set; } = AttentionState.Unknown;

    public void Attach()
    {
        if (_control.Connection is null) return;
        _connection = _control.Connection;
        _previousIntercept = _connection.InterceptOutputToUITerminal;
        _ourIntercept = (ref Span<char> s) =>
        {
            if (!_detached) OnChunk(new string(s));
            _previousIntercept?.Invoke(ref s);
        };
        _connection.InterceptOutputToUITerminal = _ourIntercept;
    }

    /// <summary>
    /// Stops parsing OSC titles and (best-effort) unhooks from the connection's
    /// intercept chain. Called once a hook event proves the primary HooksGateway
    /// signal is alive — keeping the OSC fallback running is wasted work on the
    /// PTY read-loop thread. Idempotent.
    /// </summary>
    public void Detach()
    {
        _detached = true;
        var conn = _connection;
        if (conn is null) return;
        // Only unhook if we're still the outermost intercept; otherwise the
        // _detached guard above is enough to skip the work without disturbing
        // anything chained after us.
        if (ReferenceEquals(conn.InterceptOutputToUITerminal, _ourIntercept))
            conn.InterceptOutputToUITerminal = _previousIntercept;
    }

    private void OnChunk(string chunk)
    {
        _buffer.Append(chunk);
        if (_buffer.Length > 8192) _buffer.Remove(0, _buffer.Length - 4096);

        var matches = OscTitle.Matches(_buffer.ToString());
        if (matches.Count == 0) return;

        var last = matches[^1].Groups[1].Value;
        var next = ClassifyTitle(last);
        if (next == State) return;

        State = next;
        StateChanged?.Invoke(this, next);
    }

    private static AttentionState ClassifyTitle(string title)
    {
        if (string.IsNullOrEmpty(title)) return AttentionState.Unknown;
        foreach (var ch in title)
        {
            if (BrailleSpinnerChars.IndexOf(ch) >= 0) return AttentionState.Working;
        }
        if (title.Contains("Claude Code", StringComparison.OrdinalIgnoreCase)) return AttentionState.Idle;
        return AttentionState.Unknown;
    }
}
