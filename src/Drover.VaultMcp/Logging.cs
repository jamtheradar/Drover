using System;
using System.IO;

namespace Drover.VaultMcp;

// Tiny file logger for the MCP exe. Stdout is reserved for protocol —
// every diagnostic goes here (and to stderr). Single-file rotation at
// 2 MB matches Drover.App's AppLog so the two logs sit side-by-side
// with familiar shapes.
internal static class Logging
{
    private const long RotateAtBytes = 2 * 1024 * 1024;
    private static readonly object _lock = new();
    private static readonly string _logPath = ResolveLogPath();

    public static string LogFilePath => _logPath;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
    {
        // Paranoia per the spec: include exception type + message but
        // not the inner exception trail, which can echo user-supplied
        // paths. Outer message + type is enough to triage.
        var line = ex is null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}";
        Write("ERROR", line);
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        try { Console.Error.WriteLine(line); } catch { /* stderr closed */ }

        lock (_lock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                Rotate();
                File.AppendAllText(_logPath, line + Environment.NewLine);
            }
            catch
            {
                // Swallow — the stderr mirror above is the lifeline if
                // %APPDATA% is unwriteable.
            }
        }
    }

    private static void Rotate()
    {
        try
        {
            var info = new FileInfo(_logPath);
            if (!info.Exists || info.Length < RotateAtBytes) return;
            var rotated = _logPath + ".1";
            try { File.Delete(rotated); } catch { }
            File.Move(_logPath, rotated);
        }
        catch { /* best-effort */ }
    }

    private static string ResolveLogPath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Drover", "logs", "vaultmcp.log");
    }
}
