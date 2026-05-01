using System.IO;
using Microsoft.Terminal.Wpf;

namespace Drover.App.Terminal;

/// <summary>
/// <see cref="ITerminalConnection"/> backed by Windows ConPTY. Owns the
/// pseudoconsole, the input/output pipes, and the child process; raises
/// <see cref="ITerminalConnection.TerminalOutput"/> from the read-loop thread.
///
/// Two intercept hooks let consumers tee/modify the byte streams without
/// subclassing: <see cref="InterceptOutputToUITerminal"/> for PTY-to-renderer
/// bytes (used by AttentionMonitor and SessionLogger) and
/// <see cref="InterceptInputToTermApp"/> for renderer-to-PTY bytes (used by
/// ClipboardIntegration to swallow Ctrl+C when there's a selection so the
/// keystroke becomes a copy instead of a SIGINT).
/// </summary>
public sealed class ConPtyConnection : ITerminalConnection, IDisposable
{
    public delegate void InterceptDelegate(ref Span<char> str);

    public InterceptDelegate? InterceptOutputToUITerminal;
    public InterceptDelegate? InterceptInputToTermApp;

    public event EventHandler<TerminalOutputEventArgs>? TerminalOutput;

    /// <summary>
    /// Raised once the pseudoconsole + child process are up and the input
    /// writer is ready. Subscribers must be synchronous — the read loop
    /// starts as soon as this returns. The host UserControl uses the
    /// callback to Dispatcher.Invoke onto the UI thread and set
    /// Terminal.Connection = this before any output is pumped.
    /// </summary>
    public event EventHandler? Ready;

    public bool IsStarted { get; private set; }

    private const int ReadBufferSize = 16 * 1024;

    private PseudoConsole? _console;
    private PseudoConsolePipe? _inputPipe;
    private PseudoConsolePipe? _outputPipe;
    private ChildProcess? _process;
    private FileStream? _outputStream;
    private StreamWriter? _inputWriter;
    private bool _disposed;

    /// <summary>
    /// Spins up the pseudoconsole, child process, and stdin writer; raises
    /// <see cref="Ready"/> synchronously, then runs the read loop on the
    /// caller's thread until the child exits or the output pipe closes.
    /// Call from a worker thread.
    /// </summary>
    public void Start(string command, int columns, int rows,
        string? workingDirectory = null,
        System.Collections.Generic.IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        if (IsStarted) throw new InvalidOperationException("ConPtyConnection already started.");

        _inputPipe = new PseudoConsolePipe();
        _outputPipe = new PseudoConsolePipe();
        _console = PseudoConsole.Create(_inputPipe.ReadSide, _outputPipe.WriteSide, columns, rows);
        _process = ChildProcess.Start(command, _console, workingDirectory, environmentOverrides);
        _outputStream = new FileStream(_outputPipe.ReadSide, FileAccess.Read);
        _inputWriter = new StreamWriter(new FileStream(_inputPipe.WriteSide, FileAccess.Write))
        {
            AutoFlush = true
        };

        IsStarted = true;
        Ready?.Invoke(this, EventArgs.Empty);

        ReadLoop();
    }

    private void ReadLoop()
    {
        if (_outputStream is null) return;

        var buffer = new char[ReadBufferSize];
        using var reader = new StreamReader(_outputStream);
        while (true)
        {
            int read;
            try { read = reader.Read(buffer, 0, buffer.Length); }
            catch { break; }
            if (read == 0) break;

            var span = new Span<char>(buffer, 0, read);
            InterceptOutputToUITerminal?.Invoke(ref span);
            if (span.Length == 0) continue;

            try { TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(span.ToString())); }
            catch { /* renderer torn down — keep draining the pipe */ }
        }

        try { TerminalOutput?.Invoke(this, new TerminalOutputEventArgs("\r\nSession Terminated\r\n")); }
        catch { }
    }

    /// <summary>
    /// Direct write to the child's stdin. Bypasses
    /// <see cref="InterceptInputToTermApp"/> — used for synthetic input
    /// (bracketed paste, programmatic SendInput) that should not be filtered.
    /// </summary>
    public void WriteToTerm(ReadOnlySpan<char> input)
    {
        if (_inputWriter is null) return;
        if (_console is null || _console.IsDisposed) return;
        try { _inputWriter.Write(input); }
        catch { /* pipe closed — child exited */ }
    }

    /// <summary>Sends EOF to the child's stdin. Idempotent.</summary>
    public void CloseStdin()
    {
        try { _inputWriter?.Close(); } catch { }
        try { _inputWriter?.Dispose(); } catch { }
        _inputWriter = null;
    }

    /// <summary>Forcibly terminates the child process. Idempotent.</summary>
    public void KillProcess()
    {
        try { _process?.Kill(); } catch { }
    }

    /// <summary>
    /// Pushes a string to the renderer (not the PTY). Used to inject VT
    /// mode-set sequences once the renderer is connected — specifically
    /// ESC[?9001h to enable extended Win32 input mode in the renderer's
    /// keyboard encoder, which Claude Code's TUI relies on for things like
    /// Shift+Enter and modified arrow keys.
    /// </summary>
    public void RaiseRendererOutput(string s)
    {
        if (string.IsNullOrEmpty(s)) return;
        TerminalOutput?.Invoke(this, new TerminalOutputEventArgs(s));
    }

    public void Resize(int columns, int rows) => _console?.Resize(columns, rows);

    void ITerminalConnection.Start()
    {
        // No-op. The renderer's TerminalContainer.Connection setter calls
        // this when we wire ourselves up; the actual PTY start was already
        // driven by the host on a worker thread before Ready fired.
    }

    void ITerminalConnection.WriteInput(string data)
    {
        if (string.IsNullOrEmpty(data)) return;
        var span = data.ToCharArray().AsSpan();
        InterceptInputToTermApp?.Invoke(ref span);
        if (span.Length == 0) return;
        WriteToTerm(span);
    }

    void ITerminalConnection.Resize(uint rows, uint columns) => Resize((int)columns, (int)rows);

    void ITerminalConnection.Close() => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CloseStdin();
        try { _process?.Kill(); } catch { }
        try { _process?.Dispose(); } catch { }
        _process = null;

        try { _outputStream?.Dispose(); } catch { }
        _outputStream = null;

        try { _console?.Dispose(); } catch { }
        _console = null;

        try { _inputPipe?.Dispose(); } catch { }
        _inputPipe = null;

        try { _outputPipe?.Dispose(); } catch { }
        _outputPipe = null;
    }
}
