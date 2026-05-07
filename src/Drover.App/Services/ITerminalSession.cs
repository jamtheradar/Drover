using Drover.App.Terminal;
using Drover.App.ViewModels;
using Microsoft.Terminal.Wpf;

namespace Drover.App.Services;

/// <summary>
/// Minimal abstraction over the active terminal tab so the loop runners can
/// send prompts, observe output (for the session-driven <c>[loop:*]</c> token
/// stream), and request an interrupt without dragging in the full
/// <see cref="TerminalTabViewModel"/> surface. One concrete implementation
/// today: <see cref="TerminalTabSession"/>.
/// </summary>
public interface ITerminalSession
{
    /// <summary>Push <paramref name="text"/> into the underlying ConPTY,
    /// followed by CR. Returns false if the session is not yet attached.</summary>
    bool SendInput(string text);

    /// <summary>Request a soft interrupt on the running command (Ctrl+C).
    /// Best-effort — no return value, the runner just keeps watching state.</summary>
    void SendInterrupt();

    /// <summary>Raised on the read-loop thread for every chunk of output the
    /// child process emits. Subscribers MUST marshal to the UI thread before
    /// touching ViewModel state. Used by the session-driven loop runner to
    /// parse <c>[loop:*]</c> tokens out of the stream.</summary>
    event System.EventHandler<string>? Output;
}

/// <summary>
/// Wraps a <see cref="TerminalTabViewModel"/> as an <see cref="ITerminalSession"/>.
/// Lifetime mirrors the tab — the loop runner takes a fresh adapter per run.
/// </summary>
public sealed class TerminalTabSession : ITerminalSession, System.IDisposable
{
    private readonly TerminalTabViewModel _tab;
    private ConPtyConnection? _subscribedConnection;

    public event System.EventHandler<string>? Output;

    public TerminalTabSession(TerminalTabViewModel tab)
    {
        _tab = tab;
        TrySubscribe();
    }

    public bool SendInput(string text)
    {
        TrySubscribe();
        return _tab.SendInput(text);
    }

    public void SendInterrupt()
    {
        // ConPTY accepts Ctrl+C as the literal 0x03 byte on its input stream.
        // The shell / Claude Code CLI both treat that as an interrupt request.
        _tab.SendInput("\x03", appendReturn: false);
    }

    private void TrySubscribe()
    {
        // The ConPtyConnection is created lazily inside DroverTerminal.OnLoaded,
        // so it may not exist when the runner first asks. Re-check on every
        // SendInput; once we've subscribed we keep the same connection.
        if (_subscribedConnection is not null) return;
        var conn = TabConnection(_tab);
        if (conn is null) return;
        conn.TerminalOutput += OnTerminalOutput;
        _subscribedConnection = conn;
    }

    private static ConPtyConnection? TabConnection(TerminalTabViewModel tab)
    {
        // Reach into the tab via reflection-light: the public DroverTerminal
        // surface exposes Connection, but the tab keeps the control private.
        // Fall back to a property lookup via a public accessor we add below
        // — kept simple by using the tab's internal getter.
        return tab.GetCurrentConnection();
    }

    private void OnTerminalOutput(object? sender, TerminalOutputEventArgs e)
    {
        Output?.Invoke(this, e.Data ?? string.Empty);
    }

    public void Dispose()
    {
        if (_subscribedConnection is not null)
        {
            _subscribedConnection.TerminalOutput -= OnTerminalOutput;
            _subscribedConnection = null;
        }
    }
}
