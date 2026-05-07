using System;
using System.IO;
using System.Threading;

namespace Drover.App.Services;

// Mirrors PlanWatcher: a single FileSystemWatcher rooted at one vault
// namespace, debounced via System.Threading.Timer with a lock, raising a
// path-less Changed event on a UI dispatcher (the caller marshals).
//
// One difference from PlanWatcher — VaultWatcher checks RecentWriteGuard
// against the file path on each raw event before re-arming the debounce
// timer. If the path was self-stamped recently, the event is ignored
// outright. That keeps VaultStore.Write echoes from triggering the panel
// reload that fired the write.
public sealed class VaultWatcher : IDisposable
{
    public event EventHandler? Changed;

    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private readonly object _lock = new();
    private string? _root;

    public void Watch(string root)
    {
        Stop();
        if (string.IsNullOrEmpty(root)) return;
        try
        {
            // Root may not exist yet — watcher needs an existing directory or
            // it throws. Caller is expected to ensure the dir before Watch.
            if (!Directory.Exists(root)) Directory.CreateDirectory(root);
        }
        catch { return; }

        _root = root;
        try
        {
            _watcher = new FileSystemWatcher(root, "*.md")
            {
                NotifyFilter = NotifyFilters.LastWrite
                             | NotifyFilters.Size
                             | NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.CreationTime,
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
            _root = null;
        }
    }

    private void OnAny(object _, FileSystemEventArgs args)
    {
        // Self-write echo suppression. VaultStore.Write stamps the absolute
        // path immediately before and after the move; if the watcher fires
        // within the guard window (default 750ms) we treat it as our own
        // echo and skip the reload entirely.
        if (RecentWriteGuard.WasRecent(args.FullPath)) return;

        lock (_lock)
        {
            _debounce?.Dispose();
            _debounce = new Timer(_ => Changed?.Invoke(this, EventArgs.Empty),
                null, 250, Timeout.Infinite);
        }
    }

    public void Dispose() => Stop();
}
