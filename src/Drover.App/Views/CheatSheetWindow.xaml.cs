using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drover.App.Services;

namespace Drover.App.Views;

public partial class CheatSheetWindow : Window
{
    private readonly string _fullMarkdown;

    public CheatSheetWindow()
    {
        InitializeComponent();
        _fullMarkdown = LoadBundledMarkdown();
        Render(_fullMarkdown);
        Loaded += (_, _) => SearchBox.Focus();
    }

    private static string LoadBundledMarkdown()
    {
        try
        {
            var uri = new Uri("pack://application:,,,/Resources/CheatSheet.md", UriKind.Absolute);
            var info = Application.GetResourceStream(uri);
            if (info is null) return "# Cheat sheet\n\nResource not found.";
            using var reader = new StreamReader(info.Stream);
            return reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            return $"# Cheat sheet\n\nFailed to load: {ex.Message}";
        }
    }

    private void Render(string markdown)
    {
        Viewer.Document = MarkdownRenderer.Render(markdown);
    }

    /// <summary>
    /// Filters the cheat sheet by chunking on top-level (## ) sections and keeping
    /// any section whose heading or body contains the query (case-insensitive).
    /// The intro before the first ## is always kept so the document still has a head.
    /// </summary>
    private string Filter(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return _fullMarkdown;

        var lines = _fullMarkdown.Replace("\r\n", "\n").Split('\n');
        var head = new StringBuilder();
        var sections = new System.Collections.Generic.List<StringBuilder>();
        StringBuilder? current = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                current = new StringBuilder();
                sections.Add(current);
                current.AppendLine(line);
            }
            else if (current is null)
            {
                head.AppendLine(line);
            }
            else
            {
                current.AppendLine(line);
            }
        }

        var sb = new StringBuilder();
        sb.Append(head);
        var matched = 0;
        foreach (var s in sections)
        {
            var text = s.ToString();
            if (text.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                sb.Append(text);
                matched++;
            }
        }
        StatusLine.Text = matched == 0
            ? $"No matches for \"{query}\""
            : $"{matched} section(s) match \"{query}\" · Esc to clear";
        return sb.ToString();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        Render(Filter(SearchBox.Text));
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Text = string.Empty;
                e.Handled = true;
            }
            else
            {
                Close();
                e.Handled = true;
            }
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && !SearchBox.IsKeyboardFocusWithin)
        {
            Close();
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
