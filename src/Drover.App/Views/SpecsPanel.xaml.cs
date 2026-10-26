using System.Windows;
using System.Windows.Controls;
using Drover.App.Models;
using Drover.App.ViewModels;

namespace Drover.App.Views;

public partial class SpecsPanel : UserControl
{
    public SpecsPanel()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Open the run-loop chevron's <see cref="ContextMenu"/> on click. The
    /// menu is declared on the chevron button itself so its
    /// <c>PlacementTarget</c> defaults to the button — no extra wiring.
    /// </summary>
    private void OnChevronClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.ContextMenu is null) return;
        btn.ContextMenu.IsOpen = true;
    }

    /// <summary>
    /// Double-click a row to open <see cref="SpecDetailDialog"/>. The
    /// row-level CheckBox swallows its own clicks, so a click that lands on
    /// the toggle won't bubble here. Watcher-driven reload picks up the
    /// changes after the dialog returns.
    /// </summary>
    private void OnSpecDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) return;
        if (sender is not ListBoxItem item || item.DataContext is not SpecRow row) return;

        var dialog = new SpecDetailDialog(row.File)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// Rebuilds the row context menu every time it opens. Two reasons it's all
    /// code-behind: a <see cref="ContextMenu"/> sits outside the visual tree of
    /// its placement target, so an <c>AncestorType=UserControl</c> binding from
    /// inside the row <c>DataTemplate</c> can't reach the panel ViewModel; and
    /// <see cref="Drover.App.Services.AgentCatalog"/>'s watchers can change the
    /// agent set without a tab switch, so the submenu must never be cached.
    /// </summary>
    private void OnSpecContextMenuOpened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var vm = DataContext as SpecsPanelViewModel;
        var row = ResolveRow(menu);

        var runItem = FindItem(menu, "run");
        var agentItem = FindItem(menu, "agents");

        if (vm is null || row is null)
        {
            if (runItem is not null) runItem.IsEnabled = false;
            if (agentItem is not null)
            {
                agentItem.Items.Clear();
                agentItem.IsEnabled = false;
            }
            return;
        }

        if (runItem is not null)
            runItem.IsEnabled = vm.RunSpecCommand.CanExecute(row);

        if (agentItem is null) return;
        agentItem.Items.Clear();

        if (vm.AvailableAgents.Count == 0)
        {
            agentItem.Items.Add(new MenuItem
            {
                Header = "(no agents discovered)",
                IsEnabled = false,
            });
            agentItem.IsEnabled = false;
            return;
        }

        // The submenu itself follows the dispatch command's gate, so it greys
        // out while a loop is running or when no tab is selected.
        agentItem.IsEnabled = vm.DispatchSpecToAgentCommand.CanExecute(new SpecDispatchArg(row, vm.AvailableAgents[0]));

        foreach (var a in vm.AvailableAgents)
        {
            var localAgent = a;
            var mi = new MenuItem
            {
                Header = localAgent.Name,
                ToolTip = string.IsNullOrEmpty(localAgent.Description) ? null : localAgent.Description,
            };
            mi.Click += (_, _) =>
                vm.DispatchSpecToAgentCommand.Execute(new SpecDispatchArg(row, localAgent));
            agentItem.Items.Add(mi);
        }
    }

    private void OnRunThisSpecClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SpecsPanelViewModel vm) return;
        if (ResolveRow(sender) is not { } row) return;
        if (vm.RunSpecCommand.CanExecute(row)) vm.RunSpecCommand.Execute(row);
    }

    /// <summary>
    /// Same dialog as <see cref="OnSpecDoubleClick"/> - the menu entry exists so
    /// the dialog is discoverable without knowing about double-click.
    /// </summary>
    private void OnOpenSpecClick(object sender, RoutedEventArgs e)
    {
        if (ResolveRow(sender) is not { } row) return;
        var dialog = new SpecDetailDialog(row.File)
        {
            Owner = Window.GetWindow(this),
        };
        dialog.ShowDialog();
    }

    /// <summary>
    /// A <see cref="ContextMenu"/> in a row template inherits the row's
    /// <see cref="SpecRow"/> as its DataContext, and its items inherit that in
    /// turn - so either the menu or a clicked item resolves the row.
    /// </summary>
    private static SpecRow? ResolveRow(object? source) => source switch
    {
        FrameworkElement fe when fe.DataContext is SpecRow r => r,
        _ => null,
    };

    private static MenuItem? FindItem(ContextMenu menu, string tag)
    {
        foreach (var o in menu.Items)
            if (o is MenuItem mi && (mi.Tag as string) == tag) return mi;
        return null;
    }
}
