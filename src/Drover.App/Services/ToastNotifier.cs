using System;
using Drover.App.ViewModels;
using Microsoft.Toolkit.Uwp.Notifications;

namespace Drover.App.Services;

/// <summary>
/// Wraps Microsoft.Toolkit.Uwp.Notifications for unpackaged WPF. Shows a Windows
/// toast when a background tab goes Idle. Activation brings the shell forward
/// and selects the tab by its index at the time the toast fired.
/// Any runtime failure is swallowed — the TaskbarFlash fallback still works.
/// </summary>
public sealed class ToastNotifier
{
    public event EventHandler<int>? TabClicked;
    public event EventHandler? UpdateClicked;

    public ToastNotifier()
    {
        try
        {
            ToastNotificationManagerCompat.OnActivated += toastArgs =>
            {
                try
                {
                    var args = ToastArguments.Parse(toastArgs.Argument);
                    if (args.Contains("action") && args["action"] == "update")
                    {
                        UpdateClicked?.Invoke(this, EventArgs.Empty);
                        return;
                    }
                    if (args.Contains("action") && args["action"] == "reveal" && args.Contains("path"))
                    {
                        RevealInExplorer(args["path"]);
                        return;
                    }
                    if (args.Contains("tabIndex") && int.TryParse(args["tabIndex"], out var idx))
                        TabClicked?.Invoke(this, idx);
                }
                catch { /* malformed activation — ignore */ }
            };
        }
        catch { /* pre-1903 or denied — toasts just won't work, no crash */ }
    }

    /// <summary>
    /// Opens Explorer with the given file pre-selected. Falls back to opening
    /// the parent folder if /select fails (path with non-ASCII edge cases).
    /// </summary>
    private static void RevealInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
        }
        catch
        {
            try
            {
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { /* shell verb missing — give up */ }
        }
    }

    public void NotifyUpdateAvailable(string version)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("action", "update")
                .AddText("Drover update available")
                .AddText($"Version {version} is ready — click to restart and apply.")
                .Show();
        }
        catch { /* no toast capability — ignore */ }
    }

    /// <summary>
    /// Toast confirming a history export. Clicking the toast opens Explorer
    /// with the exported file selected.
    /// </summary>
    public void NotifyExportSaved(string tabTitle, string path)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("action", "reveal")
                .AddArgument("path", path)
                .AddText("History exported")
                .AddText(tabTitle)
                .AddText(path)
                .Show();
        }
        catch { /* no toast capability — ignore */ }
    }

    public void NotifyIdle(TerminalTabViewModel tab, int tabIndex)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("tabIndex", tabIndex.ToString())
                .AddText("Claude is idle")
                .AddText(tab.Title)
                .Show();
        }
        catch { /* no toast capability on this host — ignore */ }
    }
}
