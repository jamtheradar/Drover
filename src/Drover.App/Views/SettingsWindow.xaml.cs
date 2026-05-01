using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using Drover.App.Services;
using Microsoft.Win32;

namespace Drover.App.Views;

public partial class SettingsWindow : Window
{
    private static readonly int[] Sizes = { 10, 11, 12, 13, 14, 16, 18, 20, 24 };

    // Suggestions only — DefaultModelBox is editable so users can paste any
    // alias CC supports (sonnet, opus, haiku, sonnet-4-6, etc.).
    private static readonly string[] ModelSuggestions =
    {
        "", "sonnet", "opus", "haiku", "sonnet-4-6", "opus-4-7"
    };

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

        // General tab
        BudgetBox.Text = store.Current.SessionBudgetUsd.ToString("0.##", CultureInfo.InvariantCulture);

        MdThemeBox.ItemsSource = MarkdownThemes.All.Keys.OrderBy(s => s).ToList();
        MdThemeBox.SelectedItem = MarkdownThemes.All.ContainsKey(store.Current.MarkdownTheme)
            ? store.Current.MarkdownTheme
            : MarkdownThemes.DefaultName;

        DefaultModelBox.ItemsSource = ModelSuggestions;
        DefaultModelBox.Text = store.Current.DefaultClaudeModel ?? string.Empty;

        ClaudePathBox.Text = store.Current.ClaudeExecutablePath ?? string.Empty;
        StartupBox.IsChecked = store.Current.StartWithWindows && WindowsStartupService.IsEnabled();

        if (store.Current.GlobalEnvVars is { Count: > 0 })
            GlobalEnvBox.Text = string.Join(Environment.NewLine,
                store.Current.GlobalEnvVars.Select(kv => $"{kv.Key}={kv.Value}"));

        LogKeepBox.Text = store.Current.LogKeepCount.ToString(CultureInfo.InvariantCulture);
        LogBytesBox.Text = store.Current.LogByteBudgetMb.ToString(CultureInfo.InvariantCulture);

        ApplyShortcuts(store.Current.Shortcuts ?? new KeyboardShortcuts());
    }

    private void BrowseClaude_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select claude executable",
            Filter = "Executables (*.exe;*.cmd;*.bat;*.ps1)|*.exe;*.cmd;*.bat;*.ps1|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (!string.IsNullOrWhiteSpace(ClaudePathBox.Text))
        {
            try { dlg.InitialDirectory = System.IO.Path.GetDirectoryName(ClaudePathBox.Text); } catch { }
        }
        if (dlg.ShowDialog(this) == true) ClaudePathBox.Text = dlg.FileName;
    }

    private static Dictionary<string, string>? ParseEnv(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var k = line[..eq].Trim();
            var v = line[(eq + 1)..].Trim();
            if (k.Length > 0) dict[k] = v;
        }
        return dict.Count == 0 ? null : dict;
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

        var budget = double.TryParse(BudgetBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b >= 0
            ? b : _store.Current.SessionBudgetUsd;
        var mdTheme = (MdThemeBox.SelectedItem as string) ?? MarkdownThemes.DefaultName;
        var defaultModel = (DefaultModelBox.Text ?? string.Empty).Trim();
        var claudePath = (ClaudePathBox.Text ?? string.Empty).Trim();
        var startWithWindows = StartupBox.IsChecked == true;
        var globalEnv = ParseEnv(GlobalEnvBox.Text);
        var logKeep = int.TryParse(LogKeepBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) && k >= 0
            ? k : _store.Current.LogKeepCount;
        var logBytes = int.TryParse(LogBytesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var by) && by >= 0
            ? by : _store.Current.LogByteBudgetMb;

        var shortcuts = new KeyboardShortcuts(
            CommandPalette: ScPalette.Text.Trim(),
            CycleTabForward: ScCycleNext.Text.Trim(),
            CycleTabBackward: ScCyclePrev.Text.Trim(),
            RenameTab: ScRename.Text.Trim(),
            FindInTab: ScFind.Text.Trim(),
            GlobalFind: ScGlobalFind.Text.Trim(),
            Settings: ScSettings.Text.Trim(),
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
            SessionBudgetUsd = budget,
            MarkdownTheme = mdTheme,
            DefaultClaudeModel = defaultModel,
            ClaudeExecutablePath = claudePath,
            StartWithWindows = startWithWindows,
            GlobalEnvVars = globalEnv,
            LogKeepCount = logKeep,
            LogByteBudgetMb = logBytes,
        });

        WindowsStartupService.Apply(startWithWindows);

        DialogResult = true;
    }
}
