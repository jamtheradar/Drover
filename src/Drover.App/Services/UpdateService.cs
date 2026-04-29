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
        if (!_manager.IsInstalled) return false;
        if (!await _gate.WaitAsync(0)) return false;
        try
        {
            LastError = null;
            LastCheckUtc = DateTime.UtcNow;
            var info = await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (info == null) return false;
            await _manager.DownloadUpdatesAsync(info).ConfigureAwait(false);
            PendingUpdate = info;
            UpdateReady?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
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
