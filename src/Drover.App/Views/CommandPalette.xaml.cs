using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Drover.App.ViewModels;

namespace Drover.App.Views;

public partial class CommandPalette : Window
{
    public sealed record PaletteAction(string Label, string Category, Action Execute);

    private readonly Window _owner;
    private readonly ShellViewModel _shell;
    private readonly List<PaletteAction> _allActions;

    public CommandPalette(Window owner, ShellViewModel shell)
    {
        InitializeComponent();
        _owner = owner;
        _shell = shell;
        _allActions = BuildActions();
        Results.ItemsSource = _allActions;
        if (_allActions.Count > 0) Results.SelectedIndex = 0;

        Loaded += (_, _) =>
        {
            PositionOverOwner();
            FilterBox.Focus();
        };
    }

    private void PositionOverOwner()
    {
        Left = _owner.Left + (_owner.Width - Width) / 2;
        Top = _owner.Top + 120;
    }

    private List<PaletteAction> BuildActions()
    {
        var list = new List<PaletteAction>();

        foreach (var p in _shell.Projects)
        {
            var project = p;
            list.Add(new PaletteAction(
                $"Open: {project.Name}",
                "project",
                () => _shell.OpenProjectCommand.Execute(project)));
        }

        foreach (var t in _shell.Tabs)
        {
            var tab = t;
            list.Add(new PaletteAction(
                $"Switch to: {tab.Title}",
                "tab",
                () => _shell.SelectedTab = tab));
        }

        if (_shell.SelectedTab is not null)
        {
            var currentTab = _shell.SelectedTab;
            list.Add(new PaletteAction(
                $"Close current tab ({currentTab.Title})",
                "tab",
                () => _shell.CloseTabCommand.Execute(currentTab)));
            list.Add(new PaletteAction(
                $"Pop out current tab ({currentTab.Title})",
                "tab",
                () =>
                {
                    if (_owner is ShellWindow sw) sw.PopOutTab(currentTab);
                }));
            list.Add(new PaletteAction(
                $"Open folder: {currentTab.Project.Name}",
                "tab",
                () => _shell.OpenTabFolder(currentTab)));
            list.Add(new PaletteAction(
                $"Export history (.md): {currentTab.Title}",
                "tab",
                () =>
                {
                    if (_owner is ShellWindow sw) sw.ExportHistoryFor(currentTab);
                }));
            list.Add(new PaletteAction(
                _shell.FileExplorerPanelVisible ? "Close file explorer" : "Open file explorer",
                "panel",
                () => _shell.ToggleFileExplorerPanelCommand.Execute(null)));
            var hookLabel = currentTab.HooksInstalled
                ? $"Reinstall Drover hook in {currentTab.Project.Name}"
                : $"Install Drover hook in {currentTab.Project.Name}";
            list.Add(new PaletteAction(
                hookLabel,
                "hooks",
                () => _shell.InstallHookForTab(currentTab)));
        }

        list.Add(new PaletteAction(
            "Open hooks log",
            "hooks",
            () => _shell.OpenHooksLog()));

        list.Add(new PaletteAction(
            "Run detach/reattach probe",
            "debug",
            () => _shell.StatusText = DetachReattachProbe.Run(_owner, _shell)));

        return list;
    }

    private List<PaletteAction> BuildBroadcastActions(string message)
    {
        var tabs = _shell.Tabs.ToList();
        if (tabs.Count == 0) return new List<PaletteAction>();
        var preview = message.Length > 40 ? message[..40] + "…" : message;
        return new List<PaletteAction>
        {
            new PaletteAction(
                $"Broadcast to {tabs.Count} tabs: \"{preview}\"",
                "send",
                () =>
                {
                    int sent = 0;
                    foreach (var tab in tabs)
                        if (tab.SendInput(message)) sent++;
                    _shell.StatusText = $"broadcast: sent to {sent}/{tabs.Count} tabs";
                })
        };
    }

    private List<PaletteAction> BuildSendActions(string message)
    {
        var list = new List<PaletteAction>();
        foreach (var t in _shell.Tabs)
        {
            var tab = t;
            var preview = message.Length > 40 ? message[..40] + "…" : message;
            list.Add(new PaletteAction(
                $"Send to {tab.Title}: \"{preview}\"",
                "send",
                () =>
                {
                    if (!tab.SendInput(message))
                        _shell.StatusText = $"send failed: {tab.Title} has no live PTY";
                }));
        }
        return list;
    }

    private bool IsSendMode(out string message, out bool broadcast)
    {
        var raw = FilterBox.Text;
        if (raw.StartsWith(">>"))
        {
            message = raw[2..].TrimStart();
            broadcast = true;
            return true;
        }
        if (raw.StartsWith(">"))
        {
            message = raw[1..].TrimStart();
            broadcast = false;
            return true;
        }
        message = string.Empty;
        broadcast = false;
        return false;
    }

    private void FilterBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (IsSendMode(out var message, out var broadcast))
        {
            var actions = broadcast ? BuildBroadcastActions(message) : BuildSendActions(message);
            Results.ItemsSource = actions;
            if (actions.Count > 0) Results.SelectedIndex = 0;
            return;
        }

        var q = FilterBox.Text.Trim();
        IEnumerable<PaletteAction> filtered = _allActions;
        if (q.Length > 0)
        {
            filtered = _allActions.Where(a =>
                a.Label.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                a.Category.Contains(q, StringComparison.OrdinalIgnoreCase));
        }
        var arr = filtered.ToList();
        Results.ItemsSource = arr;
        if (arr.Count > 0) Results.SelectedIndex = 0;
    }

    private void FilterBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _closing = true;
                Close();
                e.Handled = true;
                break;
            case Key.Enter:
                ExecuteSelected();
                e.Handled = true;
                break;
            case Key.Down:
                if (Results.SelectedIndex < Results.Items.Count - 1)
                    Results.SelectedIndex++;
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
            case Key.Up:
                if (Results.SelectedIndex > 0) Results.SelectedIndex--;
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
            case Key.PageDown:
                Results.SelectedIndex = System.Math.Min(Results.Items.Count - 1, Results.SelectedIndex + 8);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
            case Key.PageUp:
                Results.SelectedIndex = System.Math.Max(0, Results.SelectedIndex - 8);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
        }
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        ExecuteSelected();
    }

    private void ScrollSelectionIntoView()
    {
        if (Results.SelectedItem is null) return;
        try { Results.ScrollIntoView(Results.SelectedItem); }
        catch { /* container not realised yet — ignore */ }
    }

    /// <summary>
    /// Mouse wheel events bubble up to the focused TextBox first, which doesn't scroll
    /// — so they never reach the ListBox. Forward them explicitly so wheel-scroll works
    /// regardless of where the cursor is over the palette.
    /// </summary>
    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled) return;
        var sv = FindScrollViewer(Results);
        if (sv is null) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta);
        e.Handled = true;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            var nested = FindScrollViewer(child);
            if (nested is not null) return nested;
        }
        return null;
    }

    private bool _closing;

    private void Window_Deactivated(object sender, EventArgs e)
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void ExecuteSelected()
    {
        if (Results.SelectedItem is not PaletteAction a) return;
        _closing = true;

        // Defer the action until after the palette has fully closed so the returning
        // focus change doesn't tear down any window the action opens.
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { a.Execute(); }
            catch { /* action failure must not crash the app */ }
        }), DispatcherPriority.Background);

        Close();
    }
}
