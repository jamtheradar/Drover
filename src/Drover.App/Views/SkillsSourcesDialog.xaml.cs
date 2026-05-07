using System.Windows;
using Drover.App.Services;

namespace Drover.App.Views;

public partial class SkillsSourcesDialog : Window
{
    public string Sources
    {
        get => SourcesBox.Text;
        set => SourcesBox.Text = value;
    }

    public SkillsSourcesDialog()
    {
        InitializeComponent();
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void RestoreDefaults_Click(object sender, RoutedEventArgs e)
    {
        SourcesBox.Text = string.Join(System.Environment.NewLine, SkillsRepoCatalog.DefaultSources);
    }
}
