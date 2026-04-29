using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Drover.App.ViewModels;

namespace Drover.App.Views;

public partial class FindWindow : Window
{
    public sealed record Match(int LineNumber, string Text);

    private static readonly Regex VtStrip = new(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|[\x00-\x08\x0B\x0C\x0E-\x1F]", RegexOptions.Compiled);

    private readonly TerminalTabViewModel _tab;
    private List<string> _lines = new();

    public FindWindow(TerminalTabViewModel tab)
    {
        InitializeComponent();
        _tab = tab;
        HeaderText.Text = $"scrollback of: {tab.Title}";
        Loaded += (_, _) =>
        {
            LoadLog();
            QueryBox.Focus();
        };
    }

    private void LoadLog()
    {
        if (_tab.LogFilePath is null || !File.Exists(_tab.LogFilePath))
        {
            StatusLine.Text = "no log file yet";
            return;
        }
        try
        {
            using var fs = new FileStream(_tab.LogFilePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var raw = reader.ReadToEnd();
            raw = VtStrip.Replace(raw, string.Empty);
            _lines = raw.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            StatusLine.Text = $"{_lines.Count:N0} lines loaded";
        }
        catch (Exception ex)
        {
            StatusLine.Text = $"load failed: {ex.Message}";
        }
    }

    private void Query_Changed(object sender, TextChangedEventArgs e)
    {
        var q = QueryBox.Text;
        if (string.IsNullOrEmpty(q))
        {
            Results.ItemsSource = null;
            StatusLine.Text = $"{_lines.Count:N0} lines loaded";
            return;
        }
        var matches = new List<Match>();
        for (int i = 0; i < _lines.Count; i++)
        {
            if (_lines[i].Contains(q, StringComparison.OrdinalIgnoreCase))
                matches.Add(new Match(i + 1, _lines[i]));
            if (matches.Count >= 5000) break;
        }
        Results.ItemsSource = matches;
        if (matches.Count > 0) Results.SelectedIndex = 0;
        StatusLine.Text = $"{matches.Count:N0} matches";
    }

    private void Query_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape: Close(); e.Handled = true; break;
            case Key.Enter:
            case Key.Down:
                if (Results.SelectedIndex < Results.Items.Count - 1) Results.SelectedIndex++;
                e.Handled = true; break;
            case Key.Up:
                if (Results.SelectedIndex > 0) Results.SelectedIndex--;
                e.Handled = true; break;
        }
    }

    private void Results_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Results.SelectedItem is Match m)
        {
            try { Clipboard.SetText(m.Text); StatusLine.Text = "copied to clipboard"; }
            catch { StatusLine.Text = "copy failed"; }
        }
    }
}
