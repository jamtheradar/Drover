using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Drover.App.Models;

namespace Drover.App.Services;

public sealed class ProjectsCatalog
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _path;

    public ObservableCollection<ProjectDefinition> Projects { get; } = new();

    public ProjectsCatalog()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Drover");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "projects.json");
        Load();
    }

    public void Add(ProjectDefinition project)
    {
        Projects.Add(project);
        Save();
    }

    public void Remove(ProjectDefinition project)
    {
        if (Projects.Remove(project)) Save();
    }

    public void Replace(ProjectDefinition oldProject, ProjectDefinition newProject)
    {
        var idx = Projects.IndexOf(oldProject);
        if (idx < 0) return;
        Projects[idx] = newProject;
        Save();
    }

    private void Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                var loaded = JsonSerializer.Deserialize<List<ProjectDefinition>>(json, JsonOpts);
                if (loaded is { Count: > 0 })
                {
                    foreach (var p in loaded) Projects.Add(p);
                    return;
                }
            }
        }
        catch
        {
            // Corrupt or unreadable — start empty rather than overwrite the user's data.
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Projects, JsonOpts);
            AtomicFile.WriteAllText(_path, json);
        }
        catch
        {
            // Non-fatal — worst case, user re-adds projects next launch.
        }
    }
}
