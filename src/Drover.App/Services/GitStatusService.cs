using System.Diagnostics;
using System.IO;
using System.Windows.Threading;

namespace Drover.App.Services;

public sealed record GitStatus(bool IsRepo, string Branch, bool Dirty, int Ahead, int Behind)
{
    public static readonly GitStatus None = new(false, string.Empty, false, 0, 0);

    /// <summary>
    /// Single-line chip suitable for sidebar / tab header. Empty when not a repo.
    /// Format examples: "main", "main ●", "main ↑1", "main ●↑1↓2".
    /// </summary>
    public string ChipText
    {
        get
        {
            if (!IsRepo || string.IsNullOrEmpty(Branch)) return string.Empty;
            var marks = string.Empty;
            if (Dirty) marks += "●";
            if (Ahead > 0) marks += $"↑{Ahead}";
            if (Behind > 0) marks += $"↓{Behind}";
            return marks.Length == 0 ? Branch : $"{Branch} {marks}";
        }
    }
}

/// <summary>
/// Polls <c>git status --porcelain -b</c> for each registered project path on a
/// 5-second cadence and exposes the most recent result through <see cref="StatusChanged"/>.
/// Probes for git on PATH at <see cref="Start"/>; silently no-ops if absent so users
/// without git installed pay nothing. Per-process timeouts cap each shell-out at 3s
/// so a slow repo on a network drive can't stall the polling thread.
/// </summary>
public sealed class GitStatusService : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, GitStatus> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _watched = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _lock = new();

    private readonly DispatcherTimer _timer;
    private bool _gitOnPath;
    private bool _probed;
    private int _refreshing;

    public event EventHandler<(string Path, GitStatus Status)>? StatusChanged;

    public GitStatusService()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = PollInterval,
        };
        _timer.Tick += (_, _) => RefreshAsync();
    }

    public bool IsGitAvailable => _gitOnPath;

    /// <summary>Adds <paramref name="path"/> to the polling set. Idempotent.</summary>
    public void Watch(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        bool added;
        lock (_lock) added = _watched.Add(path);
        if (added && _gitOnPath) _ = Task.Run(() => RefreshOne(path));
    }

    /// <summary>Removes <paramref name="path"/> from the polling set.</summary>
    public void Unwatch(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_lock)
        {
            _watched.Remove(path);
            _cache.Remove(path);
        }
    }

    public GitStatus Get(string path)
    {
        lock (_lock) return _cache.TryGetValue(path, out var s) ? s : GitStatus.None;
    }

    /// <summary>
    /// Probes for <c>git --version</c> and starts the polling timer if available.
    /// Idempotent — repeated calls re-probe nothing and just ensure the timer is ticking.
    /// </summary>
    public void Start()
    {
        if (!_probed) { _gitOnPath = ProbeGit(); _probed = true; }
        if (!_gitOnPath) return;
        if (!_timer.IsEnabled) _timer.Start();
        RefreshAsync();
    }

    public void Dispose()
    {
        if (_timer.IsEnabled) _timer.Stop();
    }

    private static bool ProbeGit()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return false;
            if (!p.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
            {
                try { p.Kill(true); } catch { }
                return false;
            }
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private void RefreshAsync()
    {
        if (!_gitOnPath) return;
        // Skip if a previous refresh is still running — repos on slow filesystems can take
        // several seconds and we'd rather drop a tick than queue them up.
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;

        string[] paths;
        lock (_lock) paths = _watched.ToArray();

        Task.Run(() =>
        {
            try
            {
                foreach (var p in paths) RefreshOne(p);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private void RefreshOne(string path)
    {
        var status = Query(path);
        bool changed;
        lock (_lock)
        {
            if (!_watched.Contains(path)) return; // unwatched while we were polling
            var had = _cache.TryGetValue(path, out var prior);
            changed = !had || !prior!.Equals(status);
            _cache[path] = status;
        }
        if (!changed) return;

        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null) return;
        dispatcher.BeginInvoke(new Action(() => StatusChanged?.Invoke(this, (path, status))));
    }

    private static GitStatus Query(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return GitStatus.None;
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "status --porcelain -b")
            {
                WorkingDirectory = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p is null) return GitStatus.None;

            var stdout = p.StandardOutput.ReadToEnd();
            _ = p.StandardError.ReadToEnd();
            if (!p.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
            {
                try { p.Kill(true); } catch { }
                return GitStatus.None;
            }
            // Non-zero exit ≈ "not a git repository". Treat as no-repo and move on.
            if (p.ExitCode != 0) return GitStatus.None;
            return Parse(stdout);
        }
        catch { return GitStatus.None; }
    }

    /// <summary>
    /// Parses <c>git status --porcelain -b</c> output. The first line is always the branch
    /// header (<c>## main...origin/main [ahead 1, behind 2]</c> or <c>## HEAD (no branch)</c>);
    /// remaining lines are file change records, presence of any of which means dirty.
    /// </summary>
    internal static GitStatus Parse(string output)
    {
        if (string.IsNullOrEmpty(output)) return GitStatus.None;
        using var reader = new StringReader(output);
        var first = reader.ReadLine();
        if (first is null || !first.StartsWith("## ", StringComparison.Ordinal)) return GitStatus.None;

        var rest = first.Substring(3);
        int ahead = 0, behind = 0;
        var bracketStart = rest.IndexOf('[');
        var head = bracketStart >= 0 ? rest.Substring(0, bracketStart).TrimEnd() : rest;
        if (bracketStart >= 0)
        {
            var bracketEnd = rest.IndexOf(']', bracketStart);
            if (bracketEnd > bracketStart)
            {
                var inside = rest.Substring(bracketStart + 1, bracketEnd - bracketStart - 1);
                foreach (var raw in inside.Split(','))
                {
                    var t = raw.Trim();
                    if (t.StartsWith("ahead ", StringComparison.Ordinal) && int.TryParse(t.AsSpan(6), out var a)) ahead = a;
                    else if (t.StartsWith("behind ", StringComparison.Ordinal) && int.TryParse(t.AsSpan(7), out var b)) behind = b;
                }
            }
        }

        // Strip the "...origin/branch" suffix when present. Detached-head shows up as
        // "HEAD (no branch)" — leave it as-is so the user sees the unusual state.
        var dots = head.IndexOf("...", StringComparison.Ordinal);
        var branch = dots >= 0 ? head.Substring(0, dots) : head;

        bool dirty = false;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (!string.IsNullOrWhiteSpace(line)) { dirty = true; break; }
        }

        return new GitStatus(true, branch, dirty, ahead, behind);
    }
}
