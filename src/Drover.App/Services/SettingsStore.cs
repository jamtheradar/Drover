using System;
using System.IO;
using System.Text.Json;

namespace Drover.App.Services;

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _path;

    public AppSettings Current { get; private set; } = new();

    public event EventHandler? Changed;

    public SettingsStore()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Drover");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "settings.json");
        Load();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOpts);
            if (loaded is not null) Current = loaded;
        }
        catch { /* corrupt — use defaults */ }
    }

    public void Update(AppSettings next)
    {
        Current = next;
        Save();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Save()
    {
        try { AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(Current, JsonOpts)); }
        catch { /* non-fatal */ }
    }
}
