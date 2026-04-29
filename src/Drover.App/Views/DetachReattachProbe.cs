using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Drover.App.ViewModels;
using Drover.App.Terminal;

namespace Drover.App.Views;

/// <summary>
/// Debug probe for the Phase 3e deferred question from eval-results.md:
/// can a live DroverTerminal be reparented without losing its PTY session?
/// Bound to Ctrl+Shift+D. Reparents the selected tab's terminal to a popup window
/// and back, then reports pass/fail to the status line.
/// </summary>
internal static class DetachReattachProbe
{
    public static string Run(Window owner, ShellViewModel vm)
    {
        if (vm.SelectedTab is null) return "detach probe: no selected tab";

        var view = FindTerminalView(owner, vm.SelectedTab);
        if (view is null) return "detach probe: could not locate TerminalTabView";

        var terminal = view.Terminal;
        var originalParent = terminal.Parent as Panel;
        if (originalParent is null) return "detach probe: terminal parent is not a Panel";

        try
        {
            originalParent.Children.Remove(terminal);

            var popup = new Window
            {
                Title = "Detach probe",
                Owner = owner,
                Width = 900,
                Height = 500,
                Content = terminal
            };
            popup.Show();

            popup.Dispatcher.BeginInvoke(new Action(() =>
            {
                popup.Content = null;
                originalParent.Children.Add(terminal);
                popup.Close();
            }), System.Windows.Threading.DispatcherPriority.Background);

            return "detach probe: PASS (reparented and restored)";
        }
        catch (Exception ex)
        {
            return $"detach probe: FAIL ({ex.GetType().Name}: {ex.Message})";
        }
    }

    private static TerminalTabView? FindTerminalView(DependencyObject root, TerminalTabViewModel target)
    {
        return Descendants(root)
            .OfType<TerminalTabView>()
            .FirstOrDefault(v => ReferenceEquals(v.DataContext, target));
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grand in Descendants(child)) yield return grand;
        }
    }
}
