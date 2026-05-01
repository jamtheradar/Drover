using System.IO;
using System.Text;
using Velopack.Logging;

namespace Drover.App.Services;

/// <summary>
/// Process-wide append-only file logger at %APPDATA%\Drover\logs\app.log.
/// Used for things that aren't a terminal stream (SessionLogger covers those):
/// update checks, hook gateway errors, and unhandled exceptions.
///
/// Rotates once the active file passes <see cref="MaxBytes"/> — renames to
/// app.log.1 (overwriting any prior .1) and starts a fresh app.log. Single
/// generation kept; we never need more than the most recent failure window.
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 2 * 1024 * 1024; // 2 MB

    private static readonly object Sync = new();
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Drover", "logs");
    private static readonly string FilePath = Path.Combine(Dir, "app.log");

    public static string LogFilePath => FilePath;

    /// <summary>
    /// Deletes any file in %APPDATA%\Drover\logs older than <paramref name="maxAge"/>.
    /// Called once on startup from App.OnStartup. Best-effort: locked or in-use files
    /// (e.g. the active SessionLogger writer) are silently skipped and retried next run.
    /// </summary>
    public static void PruneOlderThan(TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(Dir)) return;
            var cutoff = DateTime.UtcNow - maxAge;
            var deleted = 0;
            foreach (var fi in new DirectoryInfo(Dir).GetFiles())
            {
                if (fi.LastWriteTimeUtc >= cutoff) continue;
                try { fi.Delete(); deleted++; } catch { /* in use — skip */ }
            }
            if (deleted > 0)
                Info("AppLog", $"Pruned {deleted} log file(s) older than {maxAge.TotalDays:0} days.");
        }
        catch (Exception ex)
        {
            Error("AppLog", "Log prune sweep failed.", ex);
        }
    }

    public static void Info(string source, string message) => Write("INFO", source, message, null);
    public static void Warn(string source, string message) => Write("WARN", source, message, null);
    public static void Error(string source, string message, Exception? ex = null) => Write("ERROR", source, message, ex);

    private static void Write(string level, string source, string message, Exception? ex)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(Dir);
                Rotate();
                var sb = new StringBuilder();
                sb.Append(DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"))
                  .Append(' ').Append(level)
                  .Append(' ').Append(source)
                  .Append(' ').Append(message);
                if (ex != null) sb.Append(' ').Append(ex);
                sb.Append(Environment.NewLine);
                File.AppendAllText(FilePath, sb.ToString(), Encoding.UTF8);
            }
        }
        catch
        {
            // Logging must never throw — if AppData is unwritable there's nothing useful to do.
        }
    }

    private static void Rotate()
    {
        try
        {
            var fi = new FileInfo(FilePath);
            if (!fi.Exists || fi.Length < MaxBytes) return;
            var bak = FilePath + ".1";
            if (File.Exists(bak)) File.Delete(bak);
            File.Move(FilePath, bak);
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Adapter so Velopack's diagnostic stream lands in the same file. Wired via
    /// <c>VelopackApp.Build().SetLogger(AppLog.VelopackLogger)</c> in Program.cs;
    /// the same instance is also passed into <see cref="Velopack.UpdateManager"/>'s
    /// source so feed-fetch errors carry through.
    /// </summary>
    public static IVelopackLogger VelopackLogger { get; } = new VelopackAdapter();

    private sealed class VelopackAdapter : IVelopackLogger
    {
        public void Log(VelopackLogLevel logLevel, string? message, Exception? exception)
        {
            message ??= string.Empty;
            switch (logLevel)
            {
                case VelopackLogLevel.Critical:
                case VelopackLogLevel.Error:
                    AppLog.Error("Velopack", message, exception);
                    break;
                case VelopackLogLevel.Warning:
                    AppLog.Warn("Velopack", message);
                    break;
                case VelopackLogLevel.Trace:
                case VelopackLogLevel.Debug:
                    // Skipped — Velopack is chatty at trace; flip on selectively if debugging.
                    break;
                default:
                    AppLog.Info("Velopack", message);
                    break;
            }
        }
    }
}
