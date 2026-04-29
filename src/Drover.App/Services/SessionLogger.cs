using System.Collections.Concurrent;
using System.IO;
using System.Text;
using Drover.App.Terminal;

namespace Drover.App.Services;

/// <summary>
/// Tees the terminal's output stream to a per-session log file under %APPDATA%\Drover\logs.
/// Stores the raw VT stream — useful for scrollback search even after the terminal's
/// rendered buffer rolls off. Chains the existing InterceptOutputToUITerminal so it
/// composes with AttentionMonitor.
///
/// Writes are queued onto a background thread so the PTY read loop never blocks on
/// disk I/O — Claude's option-picker redraws emit many small chunks, and synchronous
/// fsyncs on the read-loop thread visibly stall the renderer.
/// </summary>
public sealed class SessionLogger : IDisposable
{
    private const int FlushIntervalMs = 250;

    /// <summary>
    /// Queue payload — either text to append, or a flush sentinel. The worker
    /// thread is the sole owner of the StreamWriter, so flush requests have to
    /// route through the same queue rather than touch the writer directly.
    /// </summary>
    private readonly record struct LogItem(string? Text, ManualResetEventSlim? FlushDone);

    private readonly DroverTerminal _control;
    private StreamWriter? _writer;
    private BlockingCollection<LogItem>? _queue;
    private Thread? _worker;

    public string Path { get; }

    public SessionLogger(DroverTerminal control, string tabTitle)
    {
        _control = control;
        var dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Drover", "logs");
        Directory.CreateDirectory(dir);
        PruneOldLogs(dir, keep: 50);
        var stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var safeTitle = Sanitize(tabTitle);
        Path = System.IO.Path.Combine(dir, $"{stamp}_{safeTitle}.log");
    }

    private static void PruneOldLogs(string dir, int keep)
    {
        try
        {
            var files = new DirectoryInfo(dir).GetFiles("*.log");
            if (files.Length <= keep) return;
            System.Array.Sort(files, (a, b) => b.LastWriteTimeUtc.CompareTo(a.LastWriteTimeUtc));
            for (int i = keep; i < files.Length; i++)
            {
                try { files[i].Delete(); } catch { /* in use / locked — skip */ }
            }
        }
        catch { /* enumeration failed — non-fatal */ }
    }

    public void Attach()
    {
        if (_control.Connection is null) return;
        try
        {
            _writer = new StreamWriter(Path, append: false, Encoding.UTF8);
            _writer.WriteLine($"# Drover session log — {DateTime.Now:O}");
        }
        catch
        {
            _writer = null;
            return;
        }

        _queue = new BlockingCollection<LogItem>(new ConcurrentQueue<LogItem>());
        _worker = new Thread(WriterLoop)
        {
            IsBackground = true,
            Name = "SessionLogger.Writer"
        };
        _worker.Start();

        var previous = _control.Connection.InterceptOutputToUITerminal;
        _control.Connection.InterceptOutputToUITerminal = (ref Span<char> s) =>
        {
            // Copy the span before queuing — the underlying buffer is reused
            // by the read loop after the intercept returns.
            var queue = _queue;
            if (queue is not null && !queue.IsAddingCompleted && s.Length > 0)
            {
                try { queue.Add(new LogItem(s.ToString(), null)); } catch { /* completed during shutdown */ }
            }
            previous?.Invoke(ref s);
        };
    }

    /// <summary>
    /// Synchronously drains the write queue and flushes the StreamWriter so
    /// callers (e.g. history export) see the most recent output on disk.
    /// Bounded wait — returns silently if the worker is wedged. Safe to call
    /// from any thread.
    /// </summary>
    public void Flush()
    {
        var queue = _queue;
        if (queue is null || queue.IsAddingCompleted) return;
        using var done = new System.Threading.ManualResetEventSlim(false);
        try { queue.Add(new LogItem(null, done)); }
        catch { return; /* completed during shutdown */ }
        done.Wait(TimeSpan.FromSeconds(2));
    }

    private void WriterLoop()
    {
        var queue = _queue;
        var writer = _writer;
        if (queue is null || writer is null) return;

        while (!queue.IsCompleted)
        {
            LogItem item = default;
            bool got = false;
            try { got = queue.TryTake(out item, FlushIntervalMs); }
            catch (ObjectDisposedException) { break; }
            catch (InvalidOperationException) { break; }

            if (got)
            {
                if (item.Text is not null)
                {
                    try { writer.Write(item.Text); } catch { /* disk full / handle gone — ignore */ }
                }
                else if (item.FlushDone is not null)
                {
                    try { writer.Flush(); } catch { }
                    try { item.FlushDone.Set(); } catch { }
                }
            }
            else
            {
                // Timed out with nothing pending — flush whatever's in the StreamWriter buffer.
                try { writer.Flush(); } catch { }
            }
        }

        try { writer.Flush(); } catch { }
    }

    private static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        foreach (var ch in name)
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_');
        return sb.Length == 0 ? "tab" : sb.ToString();
    }

    public void Dispose()
    {
        try { _queue?.CompleteAdding(); } catch { }
        try { _worker?.Join(TimeSpan.FromSeconds(2)); } catch { }
        try { _queue?.Dispose(); } catch { }
        _queue = null;

        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }
}
