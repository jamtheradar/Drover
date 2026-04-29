using System.Windows;
using Drover.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Drover.App.Views;

public partial class AboutDialog : Window
{
    private readonly UpdateService? _updates;

    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var fileVer = System.Diagnostics.FileVersionInfo.GetVersionInfo(asm.Location);
        BuildText.Text = $"Build {fileVer.FileVersion}";

        _updates = (System.Windows.Application.Current as App)?.Services.GetService<UpdateService>();
        if (_updates == null || !_updates.IsInstalled)
        {
            UpdateButton.IsEnabled = false;
            UpdateStatusText.Text = "Updates available only on installed builds.";
        }
        else if (_updates.PendingUpdate != null)
        {
            ShowPendingState();
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void WhatsNew_Click(object sender, RoutedEventArgs e)
    {
        var win = new WhatsNewWindow { Owner = this };
        win.ShowDialog();
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e)
    {
        if (_updates == null || !_updates.IsInstalled) return;

        // If an update was already staged earlier this session, the second click means "apply now".
        if (_updates.PendingUpdate != null)
        {
            _updates.ApplyAndRestart();
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateStatusText.Text = "Checking…";

        var found = await _updates.CheckAsync();
        UpdateButton.IsEnabled = true;
        if (found && _updates.PendingUpdate != null)
        {
            ShowPendingState();
        }
        else if (_updates.LastError != null)
        {
            UpdateStatusText.Text = $"Update check failed: {_updates.LastError}";
        }
        else
        {
            UpdateStatusText.Text = "You're up to date.";
        }
    }

    private void ShowPendingState()
    {
        var v = _updates!.PendingUpdate!.TargetFullRelease.Version;
        UpdateButton.Content = "Restart and apply update";
        UpdateStatusText.Text = $"Version {v} is downloaded and ready.";
    }
}
