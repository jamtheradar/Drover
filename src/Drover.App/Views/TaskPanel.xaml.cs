using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drover.App.ViewModels;

namespace Drover.App.Views;

public partial class TaskPanel : UserControl
{
    public TaskPanel()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldVm) oldVm.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is INotifyPropertyChanged newVm) newVm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Auto-focus the composer textbox when the VM flips visibility on. Doing this
        // in the VM would couple it to the view; doing it here keeps the focus concern
        // where it belongs.
        if (e.PropertyName != nameof(ShellViewModel.TaskComposerVisible)) return;
        if (DataContext is not ShellViewModel vm || !vm.TaskComposerVisible) return;
        Dispatcher.BeginInvoke(new System.Action(() =>
        {
            TaskComposerBox.Focus();
            TaskComposerBox.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void TaskComposerBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        if (e.Key == Key.Enter)
        {
            vm.CommitAddTaskCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            vm.CancelAddTaskCommand.Execute(null);
            e.Handled = true;
        }
    }
}
