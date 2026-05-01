using Velopack;
using Velopack.Sources;

namespace Drover.App.Services;

/// <summary>
/// Wraps Velopack's UpdateManager against the GitHub Releases feed for
/// drover. Safe no-op when running from a dev build (i.e. not installed
/// via the Velopack bootstrapper) — IsInstalled gates everything.
/// </summary>
public sealed class UpdateService
{
    private const string GitHubRepoUrl = "https://github.com/jamtheradar/Drover";

    private readonly UpdateManager _manager;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateInfo? PendingUpdate { get; private set; }
    public string? LastError { get; private set; }
    public DateTime? LastCheckUtc { get; private set; }

    public event EventHandler? UpdateReady;

    public UpdateService()
    {
        // Velopack 0.0.1298's UpdateManager ctor takes no logger — diagnostics are
        // routed via VelopackApp.Build().SetLogger(AppLog.VelopackLogger) in Program.cs.
        _manager = new UpdateManager(new GithubSource(GitHubRepoUrl, accessToken: null, prerelease: false));
    }

    public bool IsInstalled => _manager.IsInstalled;
    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? AppInfo.Version;

    /// <summary>
    /// Checks the feed and downloads any pending update. Returns true if an update
    /// was found and staged (PendingUpdate is set, UpdateReady raised). Returns
    /// false if up-to-date, not installed, or on error (LastError set).
    /// </summary>
    public async Task<bool> CheckAsync()
    {
        if (!_manager.IsInstalled)
        {
            AppLog.Info("UpdateService", "Skipped: not running from a Velopack-installed build.");
            return false;
        }
        if (!await _gate.WaitAsync(0)) return false;
        try
        {
            LastError = null;
            LastCheckUtc = DateTime.UtcNow;
            AppLog.Info("UpdateService", $"Checking for updates against {GitHubRepoUrl} (current {CurrentVersion}).");
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info == null)
            {
                AppLog.Info("UpdateService", "No update available.");
                return false;
            }
            AppLog.Info("UpdateService", $"Update found: {info.TargetFullRelease.Version}. Downloading…");
            await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
            AppLog.Info("UpdateService", $"Update {info.TargetFullRelease.Version} downloaded and staged.");
            PendingUpdate = info;
            UpdateReady?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            AppLog.Error("UpdateService", "Update check failed.", ex);
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ApplyAndRestart()
    {
        if (PendingUpdate == null) return;
        _manager.ApplyUpdatesAndRestart(PendingUpdate);
    }
}
