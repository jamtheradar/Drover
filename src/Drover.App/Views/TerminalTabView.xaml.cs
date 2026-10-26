using System.Windows;
using System.Windows.Controls;
using Drover.App.ViewModels;

namespace Drover.App.Views;

public partial class TerminalTabView : UserControl
{
    public TerminalTabView()
    {
        InitializeComponent();
    }

    private async void UserControl_Loaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not TerminalTabViewModel vm) return;
        await vm.AttachAsync(Terminal);

        // The PTY only exists once AttachAsync returns, so a tab whose connection came
        // up after the last selection change would keep the unthrottled default output
        // rate forever — which is what session restore does to every restored tab.
        if (Window.GetWindow(this)?.DataContext is ShellViewModel shell)
            shell.SyncConnectionThrottles();
    }
}
