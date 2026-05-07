using System.Windows;

namespace Drover.App.Views;

// Modal "New note" prompt for VaultWindow. Path validation is delegated
// to the caller (which routes through VaultPaths.ResolveSafe via
// VaultStore.Write); the dialog just surfaces the resulting error in a
// red strip and stays open so the user can fix it.
public partial class NewVaultNoteDialog : Window
{
    public delegate bool TryCreateDelegate(string path, out string error);

    private readonly TryCreateDelegate _tryCreate;

    public NewVaultNoteDialog(string suggestion, TryCreateDelegate tryCreate)
    {
        InitializeComponent();
        _tryCreate = tryCreate;
        PathBox.Text = suggestion;
        Loaded += (_, _) => { PathBox.Focus(); PathBox.SelectAll(); };
    }

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var path = PathBox.Text.Trim();
        if (string.IsNullOrEmpty(path))
        {
            ShowError("Path must be non-empty.");
            return;
        }
        if (_tryCreate(path, out var error))
        {
            DialogResult = true;
            return;
        }
        ShowError(error);
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }
}
