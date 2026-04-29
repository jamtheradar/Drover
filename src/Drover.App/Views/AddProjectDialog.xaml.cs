using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Drover.App.Models;
using Microsoft.Win32;

namespace Drover.App.Views;

public partial class AddProjectDialog : Window
{
    public ProjectDefinition? Result { get; private set; }

    public AddProjectDialog(ProjectDefinition? seed = null)
    {
        InitializeComponent();
        if (seed is not null)
        {
            Title = "Edit project";
            SubmitButton.Content = "Save";
            NameBox.Text = seed.Name;
            PathBox.Text = seed.Path;
            if (seed.Kind == ProjectKind.Pwsh) KindPwsh.IsChecked = true;
            CommandBox.Text = seed.Command ?? string.Empty;
            ArgsBox.Text = seed.Args ?? string.Empty;
            PlansFolderBox.Text = seed.PlansFolder ?? string.Empty;
            TabColorBox.Text = seed.TabColor ?? string.Empty;
            if (seed.EnvVars is { Count: > 0 })
                EnvBox.Text = string.Join(Environment.NewLine, seed.EnvVars.Select(kv => $"{kv.Key}={kv.Value}"));
        }
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            UpdateTabColorSwatch();
        };
    }

    private void TabColorBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateTabColorSwatch();

    private void TabColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) TabColorBox.Text = hex;
    }

    private void TabColorClear_Click(object sender, RoutedEventArgs e)
        => TabColorBox.Text = string.Empty;

    private void UpdateTabColorSwatch()
    {
        var text = TabColorBox.Text?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            TabColorSwatch.Background = System.Windows.Media.Brushes.Transparent;
            return;
        }
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(text)!;
            TabColorSwatch.Background = new SolidColorBrush(color);
        }
        catch
        {
            TabColorSwatch.Background = System.Windows.Media.Brushes.Transparent;
        }
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Select project folder" };
        if (!string.IsNullOrWhiteSpace(PathBox.Text) && Directory.Exists(PathBox.Text))
            dlg.InitialDirectory = PathBox.Text;
        if (dlg.ShowDialog(this) == true)
        {
            PathBox.Text = dlg.FolderName;
            if (string.IsNullOrWhiteSpace(NameBox.Text))
                NameBox.Text = new DirectoryInfo(dlg.FolderName).Name;
        }
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        var path = PathBox.Text.Trim();
        if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(path)) return;

        var kind = KindPwsh.IsChecked == true ? ProjectKind.Pwsh : ProjectKind.Claude;
        var command = string.IsNullOrWhiteSpace(CommandBox.Text) ? null : CommandBox.Text.Trim();
        var args = string.IsNullOrWhiteSpace(ArgsBox.Text) ? null : ArgsBox.Text.Trim();
        var env = ParseEnv(EnvBox.Text);
        var plansFolder = string.IsNullOrWhiteSpace(PlansFolderBox.Text) ? null : PlansFolderBox.Text.Trim();
        var tabColor = string.IsNullOrWhiteSpace(TabColorBox.Text) ? null : TabColorBox.Text.Trim();

        Result = new ProjectDefinition(name, path, kind, command, args, env, plansFolder, tabColor);
        DialogResult = true;
    }

    private static Dictionary<string, string>? ParseEnv(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var k = line[..eq].Trim();
            var v = line[(eq + 1)..].Trim();
            if (k.Length > 0) dict[k] = v;
        }
        return dict.Count == 0 ? null : dict;
    }
}
