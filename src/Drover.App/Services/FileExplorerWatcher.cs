namespace Drover.App.Services;

/// <summary>
/// Recursive <see cref="System.IO.FileSystemWatcher"/> on the project root with a
/// 250 ms debounce. Pattern lifted from <see cref="PlanWatcher"/> — caller is
/// responsible for dispatching back to the UI thread before touching state.
/// </summary>
public sealed class FileExplorerWatcher : System.IDisposable
{
    public event System.EventHandler? Changed;

    private System.IO.FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private readonly object _lock = new();
    private string? _projectPath;

    /// <summary>
    /// Path fragments under the project root whose changes are ignored. Encoded as data
    /// (rather than scattered <c>.Where(...)</c> filters) so adding new ignores is one line.
    /// </summary>
    private static readonly string[] IgnoredRelativePrefixes =
    {
        // Spec workflow writes timestamped backups under .drover/history/. Without this
        // ignore, every Enhance run would burst hundreds of file events through the watcher
        // and cause UI refresh storms in the explorer pane.
        ".drover" + System.IO.Path.DirectorySeparatorChar + "history" + System.IO.Path.DirectorySeparatorChar,
        ".drover" + System.IO.Path.AltDirectorySeparatorChar + "history" + System.IO.Path.AltDirectorySeparatorChar,
    };

    public void Watch(string projectPath)
    {
        Stop();
        if (string.IsNullOrEmpty(projectPath) || !System.IO.Directory.Exists(projectPath))
            return;

        _projectPath = projectPath;

        try
        {
            _watcher = new System.IO.FileSystemWatcher(projectPath)
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite
                             | System.IO.NotifyFilters.Size
                             | System.IO.NotifyFilters.FileName
                             | System.IO.NotifyFilters.DirectoryName
                             | System.IO.NotifyFilters.CreationTime,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnAny;
            _watcher.Created += OnAny;
            _watcher.Deleted += OnAny;
            _watcher.Renamed += OnAny;
        }
        catch { _watcher = null; }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_watcher is not null)
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnAny;
                _watcher.Created -= OnAny;
                _watcher.Deleted -= OnAny;
                _watcher.Renamed -= OnAny;
                _watcher.Dispose();
                _watcher = null;
            }
            _debounce?.Dispose();
            _debounce = null;
            _projectPath = null;
        }
    }

    private void OnAny(object _, System.IO.FileSystemEventArgs e)
    {
        if (IsIgnored(e.FullPath)) return;
        lock (_lock)
        {
            _debounce?.Dispose();
            _debounce = new System.Threading.Timer(_ => Changed?.Invoke(this, System.EventArgs.Empty),
                null, 250, System.Threading.Timeout.Infinite);
        }
    }

    private bool IsIgnored(string fullPath)
    {
        var root = _projectPath;
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(fullPath)) return false;
        if (!fullPath.StartsWith(root, System.StringComparison.OrdinalIgnoreCase)) return false;
        var rel = fullPath.Substring(root.Length).TrimStart(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar);
        foreach (var prefix in IgnoredRelativePrefixes)
        {
            if (rel.StartsWith(prefix, System.StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public void Dispose() => Stop();
}
