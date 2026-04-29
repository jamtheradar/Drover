using System;
using System.Windows;
using System.Windows.Controls;
using Drover.App.ViewModels;
using Drover.App.Terminal;

namespace Drover.App.Views;

/// <summary>
/// Hosts a live DroverTerminal that was reparented out of a TerminalTabView.
/// On close, hands the control back to the origin Panel so the tab becomes live again.
/// </summary>
public partial class PoppedOutWindow : Window
{
    private readonly DroverTerminal _terminal;
    private readonly Panel _originPanel;
    private readonly TerminalTabViewModel _tab;

    public PoppedOutWindow(TerminalTabViewModel tab, DroverTerminal terminal, Panel originPanel)
    {
        InitializeComponent();
        _tab = tab;
        _terminal = terminal;
        _originPanel = originPanel;
        DataContext = tab;
        Host.Children.Add(_terminal);
        Closing += OnClosing;
    }

    private void Reattach_Click(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_terminal.Parent is Panel p) p.Children.Remove(_terminal);
        if (!_originPanel.Children.Contains(_terminal))
            _originPanel.Children.Add(_terminal);
    }
}
