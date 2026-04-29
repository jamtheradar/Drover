namespace Drover.App.Services;

/// <summary>
/// Watches both the project root (for `PLAN.md`) and the plans folder
/// (recursively, to catch `done/*.md` moves) and raises a single debounced
/// `Changed` event whenever either side changes. Caller is responsible for
/// dispatching back onto the UI thread.
/// </summary>
public sealed class PlanWatcher : System.IDisposable
{
    public event System.EventHandler? Changed;

    private System.IO.FileSystemWatcher? _rootWatcher;
    private System.IO.FileSystemWatcher? _folderWatcher;
    private System.Threading.Timer? _debounce;
    private readonly object _lock = new();

    public void Watch(string projectPath, string plansFolderRelative)
    {
        Stop();
        if (string.IsNullOrEmpty(projectPath) || !System.IO.Directory.Exists(projectPath))
            return;

        // Root: watch the project root for PLAN.md only (filter, non-recursive).
        try
        {
            _rootWatcher = new System.IO.FileSystemWatcher(projectPath, "PLAN.md")
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite
                             | System.IO.NotifyFilters.Size
                             | System.IO.NotifyFilters.FileName
                             | System.IO.NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _rootWatcher.Changed += OnAny;
            _rootWatcher.Created += OnAny;
            _rootWatcher.Deleted += OnAny;
            _rootWatcher.Renamed += OnAny;
        }
        catch { _rootWatcher = null; }

        // Folder: watch <plansFolder>/ recursively so we catch done/ moves.
        var folderAbs = System.IO.Path.Combine(projectPath, plansFolderRelative);
        if (System.IO.Directory.Exists(folderAbs))
        {
            try
            {
                _folderWatcher = new System.IO.FileSystemWatcher(folderAbs, "*.md")
                {
                    NotifyFilter = System.IO.NotifyFilters.LastWrite
                                 | System.IO.NotifyFilters.Size
                                 | System.IO.NotifyFilters.FileName
                                 | System.IO.NotifyFilters.DirectoryName
                                 | System.IO.NotifyFilters.CreationTime,
                    IncludeSubdirectories = true,
                    EnableRaisingEvents = true,
                };
                _folderWatcher.Changed += OnAny;
                _folderWatcher.Created += OnAny;
                _folderWatcher.Deleted += OnAny;
                _folderWatcher.Renamed += OnAny;
            }
            catch { _folderWatcher = null; }
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            DisposeWatcher(ref _rootWatcher);
            DisposeWatcher(ref _folderWatcher);
            _debounce?.Dispose();
            _debounce = null;
        }
    }

    private void DisposeWatcher(ref System.IO.FileSystemWatcher? w)
    {
        if (w is null) return;
        w.EnableRaisingEvents = false;
        w.Changed -= OnAny;
        w.Created -= OnAny;
        w.Deleted -= OnAny;
        w.Renamed -= OnAny;
        w.Dispose();
        w = null;
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
