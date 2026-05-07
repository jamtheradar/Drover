using System.Windows;
using System.Windows.Controls;
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
}
