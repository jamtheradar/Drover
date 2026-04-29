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

    public void Watch(string projectPath)
    {
        Stop();
        if (string.IsNullOrEmpty(projectPath) || !System.IO.Directory.Exists(projectPath))
            return;

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
        }
    }

    private void OnAny(object _, System.IO.FileSystemEventArgs __)
    {
        lock (_lock)
        {
            _debounce?.Dispose();
            _debounce = new System.Threading.Timer(_ => Changed?.Invoke(this, System.EventArgs.Empty),
                null, 250, System.Threading.Timeout.Infinite);
        }
    }

    public void Dispose() => Stop();
}
