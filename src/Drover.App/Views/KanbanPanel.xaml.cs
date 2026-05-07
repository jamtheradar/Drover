using System.Windows;
using System.Windows.Controls;
using Drover.App.Models;
using Drover.App.ViewModels;

namespace Drover.App.Views;

public partial class KanbanPanel : UserControl
{
    public KanbanPanel()
    {
        InitializeComponent();
    }

    // Drag/drop wiring lands in Step 3. The handlers must exist now because the XAML
    // references them on the column Borders; today they no-op so columns are inert.
    private void OnColumnDragOver(object sender, DragEventArgs e)
    {
        if (DataContext is not KanbanViewModel vm || !vm.SupportsDrag)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = e.Data.GetDataPresent(typeof(KanbanCard)) ? DragDropEffects.Move : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnColumnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not KanbanViewModel vm || !vm.SupportsDrag) return;
        if (e.Data.GetData(typeof(KanbanCard)) is not KanbanCard card) return;
        if (sender is not FrameworkElement fe || fe.Tag is not string tag) return;

        var target = tag switch
        {
            "Pending" => KanbanStatus.Pending,
            "Planned" => KanbanStatus.Planned,
            "InProgress" => KanbanStatus.InProgress,
            "Completed" => KanbanStatus.Completed,
            _ => card.Status,
        };
        if (target == card.Status) return;
        vm.MoveCardCommand.Execute(new MoveCardArg(card, target));
        e.Handled = true;
    }
}
