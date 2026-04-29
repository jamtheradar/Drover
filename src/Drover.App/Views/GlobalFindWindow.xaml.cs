using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Drover.App.Views;

public partial class GlobalFindWindow : Window
{
    public sealed record Hit(string LogName, string LogPath, int LineNumber, string Text);

    private static readonly Regex VtStrip = new(
        @"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\x07\x1B]*(?:\x07|\x1B\\)|[\x00-\x08\x0B\x0C\x0E-\x1F]",
        RegexOptions.Compiled);

    private CancellationTokenSource? _cts;

    public GlobalFindWindow()
    {
        InitializeComponent();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Drover", "logs");
        Directory.CreateDirectory(dir);
        var count = Directory.EnumerateFiles(dir, "*.log").Count();
        HeaderText.Text = $"{count:N0} session log(s) in {dir}";
        Loaded += (_, _) => QueryBox.Focus();
    }

    private void Query_Changed(object sender, TextChangedEventArgs e)
    {
        _cts?.Cancel();
        var q = QueryBox.Text;
        if (string.IsNullOrEmpty(q))
        {
            Results.ItemsSource = null;
            StatusLine.Text = string.Empty;
            return;
        }
        _cts = new CancellationTokenSource();
        _ = RunSearch(q, _cts.Token);
    }

    private async Task RunSearch(string query, CancellationToken ct)
    {
        StatusLine.Text = "searching…";
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Drover", "logs");
        var files = Directory.EnumerateFiles(dir, "*.log").OrderByDescending(f => f).ToList();

        var hits = await Task.Run(() =>
        {
            var list = new List<Hit>();
            foreach (var path in files)
            {
                if (ct.IsCancellationRequested) break;
                string raw;
                try
                {
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                    using var reader = new StreamReader(fs);
                    raw = reader.ReadToEnd();
                }
                catch { continue; }
                var clean = VtStrip.Replace(raw, string.Empty);
                var lines = clean.Split('\n');
                var name = Path.GetFileNameWithoutExtension(path);
                for (int i = 0; i < lines.Length; i++)
                {
                    if (ct.IsCancellationRequested) return list;
                    if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase))
                        list.Add(new Hit(name, path, i + 1, lines[i].TrimEnd('\r')));
                    if (list.Count >= 5000) return list;
                }
            }
            return list;
        }, ct);

        if (ct.IsCancellationRequested) return;
        Results.ItemsSource = hits;
        if (hits.Count > 0) Results.SelectedIndex = 0;
        StatusLine.Text = $"{hits.Count:N0} match(es) across {files.Count:N0} log(s)";
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
        if (Results.SelectedItem is not Hit h) return;
        try { Process.Start(new ProcessStartInfo(h.LogPath) { UseShellExecute = true }); }
        catch { StatusLine.Text = "open failed"; }
    }
}
