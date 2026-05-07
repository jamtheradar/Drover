using System.Collections.Concurrent;

namespace Drover.App.Services;

/// <summary>
/// Tracks recent self-writes so file-watcher-driven reloaders can ignore the echo. The
/// writer stamps a path immediately before flushing; readers consult <see cref="WasRecent"/>
/// to decide whether to skip a watcher fire.
///
/// 750 ms default window comfortably exceeds the 250 ms PlanWatcher/TaskWatcher debounce.
/// </summary>
public static class RecentWriteGuard
{
    private static readonly ConcurrentDictionary<string, DateTime> _stamps = new(System.StringComparer.OrdinalIgnoreCase);

    public static void Stamp(string absolutePath)
    {
        if (string.IsNullOrEmpty(absolutePath)) return;
        _stamps[absolutePath] = DateTime.UtcNow;
    }

    public static bool WasRecent(string absolutePath, TimeSpan window)
    {
        if (string.IsNullOrEmpty(absolutePath)) return false;
        if (!_stamps.TryGetValue(absolutePath, out var t)) return false;
        return (DateTime.UtcNow - t) < window;
    }

    /// <summary>Default 750ms window — beats the 250ms watcher debounce with margin.</summary>
    public static bool WasRecent(string absolutePath) => WasRecent(absolutePath, TimeSpan.FromMilliseconds(750));
}
