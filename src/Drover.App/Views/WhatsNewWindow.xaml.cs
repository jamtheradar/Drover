using System.IO;
using System.Windows;
using Drover.App.Services;

namespace Drover.App.Views;

public partial class WhatsNewWindow : Window
{
    public WhatsNewWindow()
    {
        InitializeComponent();
        SubtitleText.Text = $"Version {AppInfo.Version} · changes since your last launch";
        Viewer.Document = MarkdownRenderer.Render(LoadBundledMarkdown());
    }

    private static string LoadBundledMarkdown()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/WhatsNew.md", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info is null) return "# What's new\n\nResource not found.";
            using var reader = new StreamReader(info.Stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"# What's new\n\nFailed to load: {ex.Message}";
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
