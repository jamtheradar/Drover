using System.Linq;
using System.Windows;
using System.Windows.Media;
using Drover.App.Services;

namespace Drover.App.Views;

public partial class SettingsWindow : Window
{
    private static readonly int[] Sizes = { 10, 11, 12, 13, 14, 16, 18, 20, 24 };

    private readonly SettingsStore _store;

    public SettingsWindow(SettingsStore store)
    {
        InitializeComponent();
        _store = store;

        var monospaced = Fonts.SystemFontFamilies
            .Select(f => f.Source)
            .OrderBy(s => s)
            .ToList();
        FontBox.ItemsSource = monospaced;
        FontBox.SelectedItem = monospaced.Contains(store.Current.FontFamily) ? store.Current.FontFamily : "Cascadia Code";

        SizeBox.ItemsSource = Sizes;
        SizeBox.SelectedItem = Sizes.Contains(store.Current.FontSize) ? store.Current.FontSize : 12;

        ResumeBox.IsChecked = store.Current.ResumeOnRestore;
        TakeOverStatusLineBox.IsChecked = store.Current.TakeOverStatusLine;
        HooksEnabledBox.IsChecked = store.Current.HooksEnabled;
        IdleHookEnabledBox.IsChecked = store.Current.IdleHookEnabled;
        HooksDebugLoggingBox.IsChecked = store.Current.HooksDebugLogging;

        ApplyShortcuts(store.Current.Shortcuts ?? new KeyboardShortcuts());
    }

    private void ApplyShortcuts(KeyboardShortcuts sc)
    {
        ScPalette.Text = sc.CommandPalette;
        ScCycleNext.Text = sc.CycleTabForward;
        ScCyclePrev.Text = sc.CycleTabBackward;
        ScRename.Text = sc.RenameTab;
        ScFind.Text = sc.FindInTab;
        ScGlobalFind.Text = sc.GlobalFind;
        ScSettings.Text = sc.Settings;
        ScDetach.Text = sc.DetachProbe;
        ScPlanPanel.Text = sc.TogglePlanPanel;
        ScTaskPanel.Text = sc.ToggleTaskPanel;
        ScFileExplorerPanel.Text = sc.ToggleFileExplorerPanel;
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        ApplyShortcuts(new KeyboardShortcuts());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var fam = (FontBox.SelectedItem as string) ?? "Cascadia Code";
        var size = SizeBox.SelectedItem is int s ? s : 12;
        var resume = ResumeBox.IsChecked == true;
        var takeOverStatusLine = TakeOverStatusLineBox.IsChecked == true;
        var hooksEnabled = HooksEnabledBox.IsChecked == true;
        var idleHookEnabled = IdleHookEnabledBox.IsChecked == true;
        var hooksDebugLogging = HooksDebugLoggingBox.IsChecked == true;
        var shortcuts = new KeyboardShortcuts(
            CommandPalette: ScPalette.Text.Trim(),
            CycleTabForward: ScCycleNext.Text.Trim(),
            CycleTabBackward: ScCyclePrev.Text.Trim(),
            RenameTab: ScRename.Text.Trim(),
            FindInTab: ScFind.Text.Trim(),
            GlobalFind: ScGlobalFind.Text.Trim(),
            Settings: ScSettings.Text.Trim(),
            DetachProbe: ScDetach.Text.Trim(),
            TogglePlanPanel: ScPlanPanel.Text.Trim(),
            ToggleTaskPanel: ScTaskPanel.Text.Trim(),
            ToggleFileExplorerPanel: ScFileExplorerPanel.Text.Trim());
        _store.Update(_store.Current with
        {
            FontFamily = fam,
            FontSize = size,
            ResumeOnRestore = resume,
            TakeOverStatusLine = takeOverStatusLine,
            HooksEnabled = hooksEnabled,
            IdleHookEnabled = idleHookEnabled,
            HooksDebugLogging = hooksDebugLogging,
            Shortcuts = shortcuts,
        });
        DialogResult = true;
    }
}
