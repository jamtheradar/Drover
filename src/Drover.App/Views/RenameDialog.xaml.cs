using System.Windows;

namespace Drover.App.Views;

public partial class RenameDialog : Window
{
    public string? NewName { get; private set; }

    public RenameDialog(string current)
    {
        InitializeComponent();
        NameBox.Text = current;
        Loaded += (_, _) => { NameBox.Focus(); NameBox.SelectAll(); };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;
        NewName = name;
        DialogResult = true;
    }
}
