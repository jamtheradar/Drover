using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Drover.App.Models;
using Drover.App.Services;
using Drover.App.ViewModels;

namespace Drover.App.Views;

// Modeless window over `SkillsViewModel`. The window owns the VM (one VM per
// open window) so closing it cancels the in-flight load; the VM is constructed
// inline rather than resolved from DI because it depends on a project + tab
// pair only the caller has.
public partial class SkillsWindow : Window
{
    private readonly SkillsViewModel _vm;
    private readonly TerminalTabViewModel _tab;

    public SkillsWindow(ProjectDefinition project, TerminalTabViewModel tab, SkillsRepoCatalog repoCatalog, SettingsStore settingsStore)
    {
        InitializeComponent();
        _tab = tab;
        _vm = new SkillsViewModel(repoCatalog, settingsStore, project);
        _vm.PropertyChanged += OnVmPropertyChanged;
        DataContext = _vm;
        TitleText.Text = $"Add skill — {project.Name}";
        Title = $"Add skill — {project.Name}";

        Loaded += async (_, _) =>
        {
            await _vm.LoadAsync();
            UpdateStatusOverlay();
        };
        Closed += (_, _) =>
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await _vm.RefreshAsync();
        UpdateStatusOverlay();
    }

    private async void Sources_Click(object sender, RoutedEventArgs e)
    {
        var current = string.Join(System.Environment.NewLine, _vm.CurrentSources());
        var dlg = new SkillsSourcesDialog
        {
            Owner = this,
            Sources = current,
        };
        if (dlg.ShowDialog() != true) return;
        var lines = dlg.Sources.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
        var cleaned = new List<string>();
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0 && !trimmed.StartsWith("#")) cleaned.Add(trimmed);
        }
        await _vm.UpdateSourcesAsync(cleaned);
        UpdateStatusOverlay();
    }

    private void Source_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true });
            e.Handled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Open link failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(new Action(UpdateStatusOverlay));
    }

    private void UpdateStatusOverlay()
    {
        var tb = StatusOverlay;
        if (tb is null) return;
        var hasSearch = !string.IsNullOrWhiteSpace(_vm.SearchText)
            || !string.Equals(_vm.SelectedCategory, SkillsViewModel.AllCategoriesLabel, StringComparison.Ordinal);
        switch (_vm.State)
        {
            case SkillsViewModel.LoadState.Loading:
                tb.Text = "Loading from local index…";
                tb.Visibility = Visibility.Visible;
                break;
            case SkillsViewModel.LoadState.Error:
                tb.Text = $"Couldn't load skills.\n{_vm.ErrorText}\nClick Refresh to retry.";
                tb.Visibility = Visibility.Visible;
                break;
            case SkillsViewModel.LoadState.Empty:
                tb.Text = hasSearch ? "No skills match." : "No skills indexed yet. Click Refresh.";
                tb.Visibility = Visibility.Visible;
                break;
            default:
                tb.Visibility = _vm.SkillsView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
                tb.Text = _vm.SkillsView.IsEmpty ? "No skills match." : string.Empty;
                break;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
        base.OnPreviewKeyDown(e);
    }
}
