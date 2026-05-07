namespace Drover.App.Services;

/// <summary>
/// Watches a project's <c>specs/</c> folder for spec frontmatter changes
/// (created / edited / renamed / deleted), with the same 250 ms debounce
/// pattern as <see cref="PlanWatcher"/> and <see cref="TaskWatcher"/>.
///
/// Caller is responsible for marshalling <see cref="Changed"/> back onto the
/// UI thread — the underlying <see cref="System.IO.FileSystemWatcher"/>
/// raises on a worker thread.
/// </summary>
public sealed class SpecWatcher : System.IDisposable
{
    public event System.EventHandler? Changed;

    private System.IO.FileSystemWatcher? _watcher;
    private System.Threading.Timer? _debounce;
    private readonly object _lock = new();

    /// <summary>Absolute path to the watched specs folder, or empty when stopped.</summary>
    public string SpecsFolder { get; private set; } = string.Empty;

    public void Watch(string projectPath)
    {
        Stop();
        if (string.IsNullOrEmpty(projectPath) || !System.IO.Directory.Exists(projectPath))
            return;

        var specsDir = System.IO.Path.Combine(projectPath, "specs");
        if (!System.IO.Directory.Exists(specsDir))
        {
            // Specs folder may not exist yet; the watcher will be re-armed
            // when the user runs Initialize / authors the first spec. The
            // SpecsPanel re-evaluates on tab change.
            return;
        }

        try
        {
            _watcher = new System.IO.FileSystemWatcher(specsDir, "*.md")
            {
                NotifyFilter = System.IO.NotifyFilters.LastWrite
                             | System.IO.NotifyFilters.Size
                             | System.IO.NotifyFilters.FileName
                             | System.IO.NotifyFilters.CreationTime,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += OnAny;
            _watcher.Created += OnAny;
            _watcher.Deleted += OnAny;
            _watcher.Renamed += OnAny;
            SpecsFolder = specsDir;
        }
        catch
        {
            _watcher = null;
            SpecsFolder = string.Empty;
        }
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
            SpecsFolder = string.Empty;
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
