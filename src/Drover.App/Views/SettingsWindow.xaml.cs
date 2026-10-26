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

        PanelSizeBox.ItemsSource = Sizes;
        PanelSizeBox.SelectedItem = Sizes.Contains(store.Current.PanelFontSize) ? store.Current.PanelFontSize : 12;

        StatusBarSizeBox.ItemsSource = Sizes;
        StatusBarSizeBox.SelectedItem = Sizes.Contains(store.Current.StatusBarFontSize) ? store.Current.StatusBarFontSize : 11;

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
        CodexPathBox.Text = store.Current.CodexExecutablePath ?? string.Empty;
        GeminiPathBox.Text = store.Current.GeminiExecutablePath ?? string.Empty;
        StartupBox.IsChecked = store.Current.StartWithWindows && WindowsStartupService.IsEnabled();
        ShellIntegrationBox.IsChecked = store.Current.ShellIntegration;
        ShowTopBarBox.IsChecked = store.Current.ShowTopBar;
        WorkflowFeaturesBox.IsChecked = store.Current.WorkflowFeatures;
        DisableMouseClicksBox.IsChecked = store.Current.DisableClaudeMouseClicks;

        if (store.Current.GlobalEnvVars is { Count: > 0 })
            GlobalEnvBox.Text = string.Join(Environment.NewLine,
                store.Current.GlobalEnvVars.Select(kv => $"{kv.Key}={kv.Value}"));

        LogKeepBox.Text = store.Current.LogKeepCount.ToString(CultureInfo.InvariantCulture);
        LogBytesBox.Text = store.Current.LogByteBudgetMb.ToString(CultureInfo.InvariantCulture);

        // Match the persisted enum value to the Tag on each ComboBoxItem; default to
        // Transcript (the index-1 item) if the saved value is somehow stale.
        var levelTag = store.Current.SessionLogLevel.ToString();
        LogLevelBox.SelectedIndex = 1;
        for (int i = 0; i < LogLevelBox.Items.Count; i++)
        {
            if (LogLevelBox.Items[i] is System.Windows.Controls.ComboBoxItem item
                && item.Tag is string tag
                && string.Equals(tag, levelTag, StringComparison.OrdinalIgnoreCase))
            {
                LogLevelBox.SelectedIndex = i;
                break;
            }
        }

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

    private void BrowseCodex_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select codex executable",
            Filter = "Executables (*.exe;*.cmd;*.bat;*.ps1)|*.exe;*.cmd;*.bat;*.ps1|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (!string.IsNullOrWhiteSpace(CodexPathBox.Text))
        {
            try { dlg.InitialDirectory = System.IO.Path.GetDirectoryName(CodexPathBox.Text); } catch { }
        }
        if (dlg.ShowDialog(this) == true) CodexPathBox.Text = dlg.FileName;
    }

    private void BrowseGemini_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Select gemini executable",
            Filter = "Executables (*.exe;*.cmd;*.bat;*.ps1)|*.exe;*.cmd;*.bat;*.ps1|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (!string.IsNullOrWhiteSpace(GeminiPathBox.Text))
        {
            try { dlg.InitialDirectory = System.IO.Path.GetDirectoryName(GeminiPathBox.Text); } catch { }
        }
        if (dlg.ShowDialog(this) == true) GeminiPathBox.Text = dlg.FileName;
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
        ScKanbanPanel.Text = sc.ToggleKanbanPanel;
        ScSidebar.Text = sc.ToggleSidebar;
    }

    private void ResetShortcuts_Click(object sender, RoutedEventArgs e)
    {
        ApplyShortcuts(new KeyboardShortcuts());
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var fam = (FontBox.SelectedItem as string) ?? "Cascadia Code";
        var size = SizeBox.SelectedItem is int s ? s : 12;
        var panelSize = PanelSizeBox.SelectedItem is int ps ? ps : 12;
        var statusBarSize = StatusBarSizeBox.SelectedItem is int ss ? ss : 11;
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
        var codexPath = (CodexPathBox.Text ?? string.Empty).Trim();
        var geminiPath = (GeminiPathBox.Text ?? string.Empty).Trim();
        var startWithWindows = StartupBox.IsChecked == true;
        var shellIntegration = ShellIntegrationBox.IsChecked == true;
        var disableMouseClicks = DisableMouseClicksBox.IsChecked == true;
        var globalEnv = ParseEnv(GlobalEnvBox.Text);
        var logKeep = int.TryParse(LogKeepBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var k) && k >= 0
            ? k : _store.Current.LogKeepCount;
        var logBytes = int.TryParse(LogBytesBox.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var by) && by >= 0
            ? by : _store.Current.LogByteBudgetMb;

        var logLevel = _store.Current.SessionLogLevel;
        if (LogLevelBox.SelectedItem is System.Windows.Controls.ComboBoxItem chosen
            && chosen.Tag is string chosenTag
            && Enum.TryParse<SessionLogLevel>(chosenTag, ignoreCase: true, out var parsedLevel))
        {
            logLevel = parsedLevel;
        }

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
            ToggleFileExplorerPanel: ScFileExplorerPanel.Text.Trim(),
            ToggleKanbanPanel: ScKanbanPanel.Text.Trim(),
            ToggleSidebar: ScSidebar.Text.Trim());

        var conflicts = new[]
            {
                shortcuts.CommandPalette, shortcuts.CycleTabForward, shortcuts.CycleTabBackward,
                shortcuts.RenameTab, shortcuts.FindInTab, shortcuts.GlobalFind, shortcuts.Settings,
                shortcuts.TogglePlanPanel, shortcuts.ToggleTaskPanel, shortcuts.ToggleFileExplorerPanel,
                shortcuts.ToggleKanbanPanel, shortcuts.ToggleSidebar,
            }
            .Select(GestureParser.ClaudeCodeConflict)
            .Where(c => c is not null)
            .Distinct()
            .ToList();
        if (conflicts.Count > 0)
        {
            var answer = MessageBox.Show(this,
                "These Drover shortcuts will stop the key reaching Claude Code:\n\n  "
                + string.Join("\n  ", conflicts)
                + "\n\nSave anyway?",
                "Shortcut conflicts", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }
        _store.Update(_store.Current with
        {
            FontFamily = fam,
            FontSize = size,
            PanelFontSize = panelSize,
            StatusBarFontSize = statusBarSize,
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
            CodexExecutablePath = codexPath,
            GeminiExecutablePath = geminiPath,
            StartWithWindows = startWithWindows,
            ShellIntegration = shellIntegration,
            ShowTopBar = ShowTopBarBox.IsChecked == true,
            WorkflowFeatures = WorkflowFeaturesBox.IsChecked == true,
            DisableClaudeMouseClicks = disableMouseClicks,
            GlobalEnvVars = globalEnv,
            LogKeepCount = logKeep,
            LogByteBudgetMb = logBytes,
            SessionLogLevel = logLevel,
        });

        WindowsStartupService.Apply(startWithWindows);
        ShellIntegrationService.Apply(shellIntegration);

        DialogResult = true;
    }
}
