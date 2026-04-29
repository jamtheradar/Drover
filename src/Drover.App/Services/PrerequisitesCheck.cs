using System;
using System.IO;

namespace Drover.App.Services;

/// <summary>
/// Hard-dependency probes run at startup. Drover's terminal-tab spawn line
/// hard-codes <c>pwsh.exe</c>; on a box without PowerShell 7 the PTY process
/// silently fails to start and the user sees a blank terminal with a blinking
/// cursor. Catch that on launch instead of leaving them to guess.
/// </summary>
public static class PrerequisitesCheck
{
    public static bool IsPwshOnPath()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in path.Split(Path.PathSeparator))
        {
            var dir = raw.Trim().Trim('"');
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                if (File.Exists(Path.Combine(dir, "pwsh.exe"))) return true;
            }
            catch { /* malformed PATH entry — skip */ }
        }
        return false;
    }
}
