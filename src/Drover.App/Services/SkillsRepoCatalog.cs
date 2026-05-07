using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Drover.App.Models;

namespace Drover.App.Services;

// Reads the on-disk skills index produced by drover-skills-indexer.exe. Drover
// itself does no network or git work for the community catalog any more — the
// indexer mirrors repos to %APPDATA%\Drover\skills-mirror\, runs per-source
// parsers, and writes %APPDATA%\Drover\skills-index.json. We just deserialize.
//
// Refresh shells out to the indexer (synchronously waited on a background
// thread). A FileSystemWatcher on the index file picks up changes from any
// other invocation — manual run, scheduled task — without Drover needing to
// poll. Install copies blobs out of MirrorPath, no network.
//
// Per-source errors recorded by the indexer round-trip in the JSON; we surface
// them as CommunitySource.Error so a single failing repo doesn't blank the tab.
public sealed class SkillsRepoCatalog : IDisposable
{
    public static readonly IReadOnlyList<string> DefaultSources = new[]
    {
        "BehiSecc/awesome-claude-skills",
        "alirezarezvani/claude-skills",
        "davila7/claude-code-templates",
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly SettingsStore _settings;
    private readonly string _droverDir;
    private readonly string _indexPath;
    private readonly FileSystemWatcher _watcher;
    private readonly System.Windows.Threading.DispatcherTimer _debounce;

    private readonly object _lock = new();
    private SkillsIndex? _index;

    public event EventHandler? IndexChanged;

    public SkillsRepoCatalog(SettingsStore settings)
    {
        _settings = settings;
        _droverDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Drover");
        Directory.CreateDirectory(_droverDir);
        _indexPath = Path.Combine(_droverDir, "skills-index.json");

        _watcher = new FileSystemWatcher(_droverDir, "skills-index.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.CreationTime,
            EnableRaisingEvents = true,
        };
        _debounce = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ReloadFromDisk();
            IndexChanged?.Invoke(this, EventArgs.Empty);
        };
        _watcher.Changed += OnIndexChanged;
        _watcher.Created += OnIndexChanged;
        _watcher.Renamed += OnIndexChanged;
    }

    public IReadOnlyList<string> ConfiguredSources()
        => _settings.Current.SkillsSources ?? DefaultSources;

    public async Task<IReadOnlyList<CommunitySkill>> GetAllAsync(CancellationToken ct = default)
    {
        EnsureLoaded();
        var configured = ConfiguredSources();
        var merged = new List<CommunitySkill>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        SkillsIndex? snap;
        lock (_lock) snap = _index;
        if (snap is null) return merged;

        foreach (var source in configured)
        {
            var entry = snap.Sources.FirstOrDefault(s => string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            foreach (var skill in entry.Skills)
            {
                var summary = ToCommunitySkill(entry, skill);
                if (seen.Add(summary.DedupKey)) merged.Add(summary);
            }
        }
        await Task.CompletedTask;
        return merged;
    }

    public IReadOnlyList<CommunitySource> Snapshot()
    {
        EnsureLoaded();
        var configured = ConfiguredSources();
        var list = new List<CommunitySource>();
        SkillsIndex? snap;
        lock (_lock) snap = _index;
        if (snap is null) return list;

        foreach (var source in configured)
        {
            var entry = snap.Sources.FirstOrDefault(s => string.Equals(s.Source, source, StringComparison.OrdinalIgnoreCase));
            if (entry is null) continue;
            var skills = entry.Skills.Select(s => ToCommunitySkill(entry, s)).ToList();
            list.Add(new CommunitySource(
                Source: entry.Source,
                Branch: entry.Branch,
                TreeSha: entry.CommitSha ?? "",
                FetchedAt: entry.FetchedAt,
                Skills: skills,
                Error: entry.Error));
        }
        return list;
    }

    // Shells out to drover-skills-indexer.exe and waits for completion. The
    // FileSystemWatcher would catch the write anyway, but doing this inline
    // gives the caller (Refresh button) a deterministic completion signal.
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var exe = LocateIndexerExecutable();
        if (exe is null)
        {
            // Surface as a load error rather than throwing — the catch upstream
            // turns this into the Community tab's status overlay.
            throw new InvalidOperationException(
                "drover-skills-indexer.exe not found. Build the Drover.SkillsIndexer project or place the exe alongside Drover.App.exe.");
        }

        var psi = new ProcessStartInfo(exe.FileName, exe.Arguments)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start indexer: {exe.FileName}");
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        // Exit code 1 means partial success — index was still written; we
        // surface that through CommunitySource.Error from the deserialized
        // entries rather than throwing here.
        ReloadFromDisk();
        IndexChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task<string> InstallAsync(CommunitySkill skill, bool global, CancellationToken ct = default)
    {
        _ = global; // CC only honours user-level ~/.claude/skills/ today.

        // Resolve the mirror path from the index — we need it to copy files
        // out of the local clone instead of pulling raw.githubusercontent.
        EnsureLoaded();
        SkillsIndex? snap;
        lock (_lock) snap = _index;
        var entry = snap?.Sources.FirstOrDefault(s => string.Equals(s.Source, skill.Source, StringComparison.OrdinalIgnoreCase));
        if (entry is null || string.IsNullOrEmpty(entry.MirrorPath))
            throw new InvalidOperationException($"No local mirror for {skill.Source}. Run Refresh.");

        var skillsRoot = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "skills");
        var destRoot = Path.Combine(skillsRoot, skill.Slug);
        Directory.CreateDirectory(destRoot);

        foreach (var path in skill.Files)
        {
            ct.ThrowIfCancellationRequested();
            if (!path.StartsWith(skill.FolderPath, StringComparison.OrdinalIgnoreCase)) continue;
            var rel = path[skill.FolderPath.Length..].TrimStart('/');
            if (string.IsNullOrEmpty(rel)) continue;

            var src = Path.Combine(entry.MirrorPath, path.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(src)) continue;

            var destPath = Path.Combine(destRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            await Task.Run(() => File.Copy(src, destPath, overwrite: true), ct).ConfigureAwait(false);
        }
        return destRoot;
    }

    public void Dispose()
    {
        _watcher.Dispose();
        _debounce.Stop();
    }

    private static CommunitySkill ToCommunitySkill(IndexedSource source, IndexedSkill skill) =>
        new(
            Source: source.Source,
            Branch: source.Branch,
            Slug: skill.Slug,
            Name: skill.Name,
            FolderPath: skill.FolderPath,
            Description: skill.Description,
            Files: skill.Files);

    private void OnIndexChanged(object sender, FileSystemEventArgs e)
    {
        // Coalesce the file-watcher's noisy pair of events into one reload.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _debounce.Stop();
            _debounce.Start();
        });
    }

    private void EnsureLoaded()
    {
        lock (_lock) { if (_index is not null) return; }
        ReloadFromDisk();
    }

    private void ReloadFromDisk()
    {
        try
        {
            if (!File.Exists(_indexPath))
            {
                lock (_lock) _index = null;
                return;
            }
            var json = File.ReadAllText(_indexPath);
            var loaded = JsonSerializer.Deserialize<SkillsIndex>(json, JsonOpts);
            lock (_lock) _index = loaded;
        }
        catch
        {
            lock (_lock) _index = null;
        }
    }

    private sealed record IndexerLaunch(string FileName, string Arguments);

    private static IndexerLaunch? LocateIndexerExecutable()
    {
        // 1. Sibling of the running exe (production layout).
        var baseDir = AppContext.BaseDirectory;
        var sibling = Path.Combine(baseDir, "drover-skills-indexer.exe");
        if (File.Exists(sibling)) return new IndexerLaunch(sibling, "");

        // 2. Dev layout: walk up to the repo's `Project/src/` and look for the
        //    indexer's bin/<config>/net10.0/ output.
        var dir = new DirectoryInfo(baseDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "Drover.SkillsIndexer", "bin");
            if (Directory.Exists(candidate))
            {
                foreach (var config in new[] { "Debug", "Release" })
                {
                    foreach (var tfm in new[] { "net10.0", "net10.0-windows" })
                    {
                        var exe = Path.Combine(candidate, config, tfm, "drover-skills-indexer.exe");
                        if (File.Exists(exe)) return new IndexerLaunch(exe, "");
                    }
                }
            }
            dir = dir.Parent;
        }

        // 3. Last resort: `dotnet run` against the csproj if we can find it.
        var probe = new DirectoryInfo(baseDir);
        while (probe is not null)
        {
            var csproj = Path.Combine(probe.FullName, "Drover.SkillsIndexer", "Drover.SkillsIndexer.csproj");
            if (File.Exists(csproj)) return new IndexerLaunch("dotnet", $"run --project \"{csproj}\" -c Release");
            probe = probe.Parent;
        }
        return null;
    }
}

// Wire-format records — kept private to this file; Drover.App doesn't need
// the indexer's full schema visible elsewhere.
internal sealed record IndexedSkill(string Slug, string Name, string? Description, string FolderPath, IReadOnlyList<string> Files, string? Plugin = null);
internal sealed record IndexedSource(string Source, string Branch, string? CommitSha, string MirrorPath, DateTimeOffset FetchedAt, string Parser, IReadOnlyList<IndexedSkill> Skills, string? Error);
internal sealed record SkillsIndex(int Version, DateTimeOffset GeneratedAt, IReadOnlyList<IndexedSource> Sources);
