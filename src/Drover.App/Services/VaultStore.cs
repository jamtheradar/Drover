using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using Drover.Vault.Core;
using Drover.Vault.Core.Models;

namespace Drover.App.Services;

// Singleton facade over Drover.Vault.Core. Owns the active-project
// state, the global + per-project watchers, and the RecentWriteGuard
// stamping that pairs the two. Public API is scope-keyed
// (`List(VaultScope.Project, …)`) so the panel doesn't have to plumb
// a root through every call.
//
// The Changed event is raised on the WPF dispatcher (per CLAUDE.md's
// dispatcher-marshalling rule); the watcher itself runs on a thread
// pool callback.
public sealed class VaultStore : IDisposable
{
    private readonly string _globalRoot;
    private readonly VaultWatcher _globalWatcher;
    private readonly VaultWatcher _projectWatcher;

    private string? _projectVaultRoot;

    public event EventHandler? Changed;

    public string? ActiveProjectPath { get; private set; }

    public VaultStore()
    {
        _globalRoot = VaultPaths.GetGlobalRoot();
        Directory.CreateDirectory(_globalRoot);
        _globalWatcher = new VaultWatcher();
        _globalWatcher.Changed += OnWatcherChanged;
        _globalWatcher.Watch(_globalRoot);

        _projectWatcher = new VaultWatcher();
        _projectWatcher.Changed += OnWatcherChanged;
    }

    // Project namespace swap. Passing null disposes the project watcher
    // and makes subsequent VaultScope.Project calls throw — that's the
    // contract the panel relies on so a stale write can't leak into a
    // freshly closed project.
    public void SetActiveProject(string? projectPath)
    {
        ActiveProjectPath = projectPath;
        if (string.IsNullOrEmpty(projectPath))
        {
            _projectVaultRoot = null;
            _projectWatcher.Stop();
            return;
        }

        var root = VaultPaths.GetProjectRoot(projectPath);
        _projectVaultRoot = root;
        try { Directory.CreateDirectory(root); } catch { /* surface on first write */ }
        _projectWatcher.Watch(root);
    }

    public IReadOnlyList<VaultEntry> List(VaultScope scope, string? subfolder = null)
        => VaultIo.List(RootFor(scope), subfolder);

    public string Read(VaultScope scope, string relativePath)
        => VaultIo.Read(RootFor(scope), relativePath);

    public void Write(VaultScope scope, string relativePath, string content)
    {
        var root = RootFor(scope);
        var abs = VaultPaths.ResolveSafe(root, relativePath);
        // Stamp before and after the write — File.Move can take a few ms
        // and the guard is from-time-of-stamp, mirroring LocalKanbanWriter.
        RecentWriteGuard.Stamp(abs);
        VaultIo.Write(root, relativePath, content);
        RecentWriteGuard.Stamp(abs);
    }

    public void Append(VaultScope scope, string relativePath, string content)
    {
        var root = RootFor(scope);
        var abs = VaultPaths.ResolveSafe(root, relativePath);
        RecentWriteGuard.Stamp(abs);
        VaultIo.Append(root, relativePath, content);
        RecentWriteGuard.Stamp(abs);
    }

    public IReadOnlyList<VaultSearchHit> Search(VaultScope scope, string query, int maxHits = 50)
        => VaultIo.Search(RootFor(scope), query, maxHits);

    private string RootFor(VaultScope scope) => scope switch
    {
        VaultScope.Global => _globalRoot,
        VaultScope.Project => _projectVaultRoot
            ?? throw new InvalidOperationException("No active project — call SetActiveProject before using VaultScope.Project."),
        _ => throw new ArgumentOutOfRangeException(nameof(scope), scope, null),
    };

    private void OnWatcherChanged(object? _, EventArgs __)
    {
        var app = Application.Current;
        if (app is null)
        {
            // No WPF app context (test harness, headless): raise inline.
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }
        app.Dispatcher.BeginInvoke(new Action(() => Changed?.Invoke(this, EventArgs.Empty)));
    }

    public void Dispose()
    {
        _globalWatcher.Changed -= OnWatcherChanged;
        _projectWatcher.Changed -= OnWatcherChanged;
        _globalWatcher.Dispose();
        _projectWatcher.Dispose();
    }
}
