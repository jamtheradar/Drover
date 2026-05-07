using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drover.App.Models;
using Drover.App.ViewModels;

namespace Drover.App.Views;

public partial class KanbanCardControl : UserControl
{
    private System.Windows.Point _pressOrigin;
    private bool _pressed;

    public KanbanCardControl()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Populate the "Send to agent ▸" submenu lazily on first context-menu open.
        // Agents can change at runtime (catalog watcher), so rebuild every time.
        if (Card.ContextMenu is { } cm) cm.Opened += OnContextMenuOpened;
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        var vm = FindKanbanViewModel();
        if (vm is null) return;
        if (SendToAgentMenu is null) return;

        SendToAgentMenu.Items.Clear();
        if (vm.AvailableAgents.Count == 0)
        {
            SendToAgentMenu.Items.Add(new MenuItem
            {
                Header = "(no agents discovered)",
                IsEnabled = false,
            });
            SendToAgentMenu.IsEnabled = false;
            return;
        }
        SendToAgentMenu.IsEnabled = true;
        if (DataContext is not KanbanCard card) return;
        foreach (var a in vm.AvailableAgents)
        {
            var mi = new MenuItem
            {
                Header = a.Name,
                ToolTip = string.IsNullOrEmpty(a.Description) ? null : a.Description,
            };
            var localAgent = a;
            mi.Click += (_, _) =>
                vm.DispatchToAgentCommand.Execute(new DispatchArg(card, localAgent));
            SendToAgentMenu.Items.Add(mi);
        }
    }

    private async void OnSubItemClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not KanbanCard card) return;
        if (card.SourceRef is not LocalFileRef lf) return;
        if (sender is not CheckBox cb) return;
        if (cb.Tag is not int lineNumber) return;
        var isDone = cb.IsChecked == true;
        await Drover.App.Services.LocalKanbanWriter.ToggleSubItemAsync(lf.AbsolutePath, lineNumber, isDone);
        // The file watcher will re-emit cards once the parser re-runs; the writer
        // also stamps RecentWriteGuard so the immediate echo is suppressed.
    }

    private void OnOpenFileClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is not KanbanCard card) return;
        if (card.SourceRef is not LocalFileRef lf) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = lf.AbsolutePath,
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    private void OnCardMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        _pressed = true;
    }

    private void OnCardMouseMove(object sender, MouseEventArgs e)
    {
        if (!_pressed || e.LeftButton != MouseButtonState.Pressed) { _pressed = false; return; }
        if (DataContext is not KanbanCard card) return;

        var here = e.GetPosition(this);
        var dx = System.Math.Abs(here.X - _pressOrigin.X);
        var dy = System.Math.Abs(here.Y - _pressOrigin.Y);
        if (dx < SystemParameters.MinimumHorizontalDragDistance &&
            dy < SystemParameters.MinimumVerticalDragDistance) return;

        var vm = FindKanbanViewModel();
        if (vm is null || !vm.SupportsDrag) { _pressed = false; return; }

        _pressed = false;
        DragDrop.DoDragDrop(this, new DataObject(typeof(KanbanCard), card), DragDropEffects.Move);
    }

    /// <summary>
    /// Click (no drag) on a local-file card opens the details dialog. Drag start
    /// clears <c>_pressed</c>, so reaching this with <c>_pressed == true</c> means
    /// the user clicked without crossing the drag threshold.
    /// </summary>
    private void OnCardMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        if (DataContext is not KanbanCard card) return;
        if (card.SourceRef is not LocalFileRef) return;

        // Suppress when the click landed on an interactive child (toggle, checkbox, button).
        // WPF buttons mark MouseLeftButtonUp as Handled; this is a belt-and-braces check
        // for the rare case where Handled propagates here despite the click being on a child.
        if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) return;

        var dialog = new KanbanCardDetailsDialog(card)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
    }

    private KanbanViewModel? FindKanbanViewModel()
    {
        DependencyObject? cur = this;
        while (cur is not null)
        {
            if (cur is FrameworkElement fe && fe.DataContext is KanbanViewModel vm) return vm;
            cur = System.Windows.Media.VisualTreeHelper.GetParent(cur);
        }
        return null;
    }
}
