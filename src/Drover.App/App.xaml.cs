using System.Windows;
using Drover.App.Services;
using Drover.App.ViewModels;
using Drover.App.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Drover.App;

public partial class App : Application
{
    private IHost? _host;

    public System.IServiceProvider Services => _host!.Services;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        MigrateLegacyAppData();

        if (!PrerequisitesCheck.IsPwshOnPath())
        {
            var result = MessageBox.Show(
                "Drover requires PowerShell 7 (pwsh.exe), which wasn't found on your PATH.\n\n" +
                "Install with:\n    winget install Microsoft.PowerShell\n\n" +
                "Or download from https://aka.ms/powershell\n\n" +
                "Continue anyway? Terminal tabs will start blank until pwsh is installed.",
                "PowerShell 7 not found",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No);
            if (result != MessageBoxResult.Yes)
            {
                Shutdown();
                return;
            }
        }

        _host = Host.CreateDefaultBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ProjectsCatalog>();
                services.AddSingleton<SessionStore>();
                services.AddSingleton<SettingsStore>();
                services.AddSingleton<ToastNotifier>();
                services.AddSingleton<ActivityLog>();
                services.AddSingleton<HooksGateway>();
                services.AddSingleton<HooksInstaller>();
                services.AddSingleton<TokenStats>();
                services.AddSingleton<TrayIconService>();
                services.AddSingleton<UpdateService>();
                services.AddSingleton<ShellViewModel>();
                services.AddSingleton<ShellWindow>();
            })
            .Build();

        // Touch the ToastNotifier early so OnActivated is wired before any toast fires.
        _host.Services.GetRequiredService<ToastNotifier>();

        // Start the hooks gateway. Failure to start (port collision) is non-fatal —
        // tabs simply launch without DROVER_HOOKS_URL set, and OSC monitoring carries on.
        var gateway = _host.Services.GetRequiredService<HooksGateway>();
        var installer = _host.Services.GetRequiredService<HooksInstaller>();
        var settingsStore = _host.Services.GetRequiredService<SettingsStore>();

        gateway.DebugLogging = settingsStore.Current.HooksDebugLogging;
        // Always start the gateway — it's a benign loopback listener that costs
        // a port and a thread. Tabs unconditionally inject DROVER_HOOKS_URL into
        // their env so the statusLine forwarder can POST even when event hooks
        // are off; gating start on HooksEnabled stranded that path.
        gateway.TryStart();

        // Reconcile settings.json on every launch — covers fresh installs (writes
        // entries) and the user disabling hooks externally then relaunching (strips
        // entries). Safe to run regardless of gateway state: when hooks are disabled
        // this just removes any prior Drover entries.
        installer.TryInstall(
            settingsStore.Current.TakeOverStatusLine,
            settingsStore.Current.HooksEnabled,
            settingsStore.Current.IdleHookEnabled);

        var lastSnapshot = (
            settingsStore.Current.TakeOverStatusLine,
            settingsStore.Current.HooksEnabled,
            settingsStore.Current.IdleHookEnabled);
        settingsStore.Changed += (_, _) =>
        {
            var s = settingsStore.Current;
            gateway.DebugLogging = s.HooksDebugLogging;

            var snapshot = (s.TakeOverStatusLine, s.HooksEnabled, s.IdleHookEnabled);
            if (snapshot == lastSnapshot) return;
            lastSnapshot = snapshot;
            installer.TryInstall(s.TakeOverStatusLine, s.HooksEnabled, s.IdleHookEnabled);
        };

        var shell = _host.Services.GetRequiredService<ShellWindow>();
        var vm = _host.Services.GetRequiredService<ShellViewModel>();
        shell.DataContext = vm;
        _host.Services.GetRequiredService<SessionStore>().BindAndRestore(vm);

        var stats = _host.Services.GetRequiredService<TokenStats>();
        stats.Updated += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new System.Action(() =>
        {
            vm.DailyCostText = $"Today ${stats.DailyCostUsd:0.00}";
            vm.DailyTokensText = $"{Format(stats.DailyInputTokens)} in / {Format(stats.DailyOutputTokens)} out";
            vm.DailyCacheText = $"{Format(stats.DailyCacheTokens)} cache";

            // CC's statusLine push is authoritative for the 5h window — once any tab has
            // reported it, OnTabStatusLineUpdated owns these fields and we leave them be.
            if (!vm.SessionFromCc)
            {
                if (stats.SessionResetUtc is { } resetUtc)
                {
                    var resetLocal = resetUtc.ToLocalTime();
                    var budget = vm.Settings.Current.SessionBudgetUsd;
                    var pct = budget > 0 ? System.Math.Min(100.0, stats.SessionCostUsd / budget * 100.0) : 0;
                    vm.SessionPercent = pct;
                    vm.SessionActive = true;
                    vm.SessionUsageText = $"Session {pct:0}%";
                    vm.SessionResetText = $"resets {resetLocal:HH:mm}";
                }
                else
                {
                    vm.SessionPercent = 0;
                    vm.SessionActive = false;
                    vm.SessionUsageText = "Session idle";
                    vm.SessionResetText = string.Empty;
                }
            }
            vm.ModelSpend.Clear();
            foreach (var row in stats.DailyByModel)
                vm.ModelSpend.Add(new ModelSpendRow(row.Model, $"${row.Cost:0.00}"));

            // Tokenomics view bindings
            vm.WeekCostText = $"${stats.Last7DaysCostUsd:0.00}";
            vm.MonthCostText = $"${stats.Last30DaysCostUsd:0.00}";
            vm.AllTimeCostText = $"${stats.AllTimeCostUsd:0.00}";
            vm.AllTimeTokensText = $"{Format(stats.AllTimeInputTokens)} in / {Format(stats.AllTimeOutputTokens)} out / {Format(stats.AllTimeCacheTokens)} cache";

            vm.DailyBars.Clear();
            var maxDay = 0.0;
            foreach (var p in stats.DailySeries) if (p.Cost > maxDay) maxDay = p.Cost;
            var todayDate = System.DateTime.UtcNow.Date;
            foreach (var p in stats.DailySeries)
            {
                var h = maxDay > 0 ? (p.Cost / maxDay) * 110.0 : 0.0;
                if (p.Cost > 0 && h < 2) h = 2;
                var label = p.Date == todayDate ? "today" : p.Date.ToString("ddd d");
                vm.DailyBars.Add(new DailyBarRow(label, p.Cost, $"${p.Cost:0.00}", h, p.Date == todayDate));
            }

            vm.ProjectSpend.Clear();
            var maxProj = 0.0;
            foreach (var pr in stats.ProjectTotals7d) if (pr.Cost > maxProj) maxProj = pr.Cost;
            foreach (var pr in stats.ProjectTotals7d)
            {
                var frac = maxProj > 0 ? pr.Cost / maxProj : 0;
                vm.ProjectSpend.Add(new ProjectSpendRow(pr.Project, $"${pr.Cost:0.00}", $"{Format(pr.Tokens)} tokens", frac));
            }

            vm.ModelSpend7d.Clear();
            foreach (var m in stats.ModelByCost7d)
                vm.ModelSpend7d.Add(new ModelSpendRow(m.Model, $"${m.Cost:0.00}"));

            vm.RecentSessions.Clear();
            foreach (var s in stats.RecentSessions)
            {
                var local = s.LastWriteUtc.ToLocalTime();
                var rel = RelativeTime(local);
                vm.RecentSessions.Add(new RecentSessionRow(rel, s.Project, s.Model ?? "—", $"${s.Cost:0.00}", $"{Format(s.Tokens)}"));
            }
        }));
        stats.Start();

        // CC's statusLine push doesn't carry thinking effort — that lives in the transcript
        // JSONL. Trigger a TokenStats refresh on every push so effort/context update on the
        // 10s push cadence rather than waiting for the next 15s polling tick.
        vm.StatusLinePushed += (_, _) => stats.Refresh();

        shell.Show();

        // Pop the What's New dialog once per version. Deferred so the shell has
        // painted before the modal appears.
        shell.Dispatcher.BeginInvoke(new System.Action(() => shell.ShowWhatsNewIfNeeded()),
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);

        // Tray icon mirrors aggregate session state (idle / working / attention /
        // inactive). Bind after the shell is up so initial Recompute sees the
        // restored tab set rather than an empty one.
        var tray = _host.Services.GetRequiredService<TrayIconService>();
        tray.Bind(vm);

        // Velopack auto-update: only meaningful when the app was installed via the
        // bootstrapper (UpdateService.IsInstalled). On a positive check, ToastNotifier
        // raises UpdateClicked when the user taps the toast — apply + restart there.
        var updates = _host.Services.GetRequiredService<UpdateService>();
        var toaster = _host.Services.GetRequiredService<ToastNotifier>();
        toaster.UpdateClicked += (_, _) => updates.ApplyAndRestart();
        if (updates.IsInstalled)
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                if (await updates.CheckAsync() && updates.PendingUpdate is { } info)
                    toaster.NotifyUpdateAvailable(info.TargetFullRelease.Version.ToString());
            });
        }
    }

    private static string Format(long n) =>
        n >= 1_000_000 ? $"{n / 1_000_000.0:0.0}M" :
        n >= 1_000 ? $"{n / 1_000.0:0.0}k" :
        n.ToString();

    private static string RelativeTime(System.DateTime local)
    {
        var delta = System.DateTime.Now - local;
        if (delta.TotalSeconds < 60) return "just now";
        if (delta.TotalMinutes < 60) return $"{(int)delta.TotalMinutes}m ago";
        if (delta.TotalHours < 24) return $"{(int)delta.TotalHours}h ago";
        if (delta.TotalDays < 7) return $"{(int)delta.TotalDays}d ago";
        return local.ToString("MMM d");
    }

    // One-shot rename of %APPDATA%\Ccc → %APPDATA%\Drover for users carried over from the
    // pre-rename build. Safe to leave in indefinitely — no-op once Drover folder exists.
    private static void MigrateLegacyAppData()
    {
        try
        {
            var appData = System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData);
            var legacy = System.IO.Path.Combine(appData, "Ccc");
            var current = System.IO.Path.Combine(appData, "Drover");
            if (System.IO.Directory.Exists(legacy) && !System.IO.Directory.Exists(current))
                System.IO.Directory.Move(legacy, current);
        }
        catch { /* non-fatal: stores will recreate from defaults */ }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _host?.Dispose();
        base.OnExit(e);
    }
}
