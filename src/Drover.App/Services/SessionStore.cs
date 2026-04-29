using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using Drover.App.ViewModels;

namespace Drover.App.Services;

/// <summary>
/// Persists the set of open tabs (project + title) to %APPDATA%\Drover\session.json so the
/// app restores the previous workspace on launch.
/// </summary>
public sealed class SessionStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public sealed record Entry(string ProjectName, string ProjectPath, string Title);

    private readonly string _path;
    private readonly ProjectsCatalog _catalog;
    private readonly SettingsStore _settings;
    private readonly HooksGateway _hooks;
    private ShellViewModel? _shell;

    public SessionStore(ProjectsCatalog catalog, SettingsStore settings, HooksGateway hooks)
    {
        _catalog = catalog;
        _settings = settings;
        _hooks = hooks;
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Drover");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "session.json");
    }

    public void BindAndRestore(ShellViewModel shell)
    {
        _shell = shell;

        Restore();

        shell.Tabs.CollectionChanged += OnTabsChanged;
        foreach (var t in shell.Tabs) t.PropertyChanged += OnTabPropertyChanged;
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (TerminalTabViewModel t in e.OldItems) t.PropertyChanged -= OnTabPropertyChanged;
        if (e.NewItems is not null)
            foreach (TerminalTabViewModel t in e.NewItems) t.PropertyChanged += OnTabPropertyChanged;
        Save();
    }

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TerminalTabViewModel.Title)) Save();
    }

    private void Restore()
    {
        if (_shell is null) return;
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var entries = JsonSerializer.Deserialize<List<Entry>>(json, JsonOpts);
            if (entries is null) return;

            foreach (var e in entries)
            {
                var project = _catalog.Projects.FirstOrDefault(p =>
                    string.Equals(p.Path, e.ProjectPath, StringComparison.OrdinalIgnoreCase));
                if (project is null) continue;
                var settings = _shell.Settings.Current;
                var tab = new TerminalTabViewModel(
                    project,
                    e.Title,
                    settings.FontFamily,
                    settings.FontSize,
                    resume: settings.ResumeOnRestore,
                    hooksUrl: _hooks.Url);
                _hooks.Register(tab.SessionId, tab.OnHookEvent);
                _hooks.RegisterStatus(tab.SessionId, tab.OnStatusLine);
                tab.StatusLineUpdated += _shell.OnTabStatusLineUpdated;
                _shell.Tabs.Add(tab);
                _shell.RefreshHooksStatusFor(project.Path);
            }
            if (_shell.Tabs.Count > 0 && _shell.SelectedTab is null)
                _shell.SelectedTab = _shell.Tabs[0];
        }
        catch
        {
            // Corrupt session — start fresh, no-op.
        }
    }

    private void Save()
    {
        if (_shell is null) return;
        try
        {
            var entries = _shell.Tabs.Select(t => new Entry(t.Project.Name, t.Project.Path, t.Title)).ToList();
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(entries, JsonOpts));
        }
        catch
        {
            // Non-fatal.
        }
    }
}
