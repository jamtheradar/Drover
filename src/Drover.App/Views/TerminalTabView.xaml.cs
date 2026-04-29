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
        if (DataContext is TerminalTabViewModel vm)
            await vm.AttachAsync(Terminal);
    }
}
