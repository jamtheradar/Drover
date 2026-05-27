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

    private static readonly string[] ModelSuggestions =
    {
        "", "sonnet", "opus", "haiku", "sonnet-4-6", "opus-4-7"
    };

    public AddProjectDialog(ProjectDefinition? seed = null)
    {
        InitializeComponent();
        ModelBox.ItemsSource = ModelSuggestions;
        if (seed is not null)
        {
            Title = "Edit project";
            SubmitButton.Content = "Save";
            NameBox.Text = seed.Name;
            PathBox.Text = seed.Path;
            if (seed.Kind == ProjectKind.Pwsh) KindPwsh.IsChecked = true;
            else if (seed.Kind == ProjectKind.ClaudeDirect) KindClaudeDirect.IsChecked = true;
            else if (seed.Kind == ProjectKind.Codex) KindCodex.IsChecked = true;
            else if (seed.Kind == ProjectKind.Gemini) KindGemini.IsChecked = true;
            CommandBox.Text = seed.Command ?? string.Empty;
            ArgsBox.Text = seed.Args ?? string.Empty;
            PlansFolderBox.Text = seed.PlansFolder ?? string.Empty;
            TabColorBox.Text = seed.TabColor ?? string.Empty;
            LaunchCommandBox.Text = seed.LaunchCommand ?? string.Empty;
            ModelBox.Text = seed.Model ?? string.Empty;
            if (seed.EnvVars is { Count: > 0 })
            {
                IEnumerable<KeyValuePair<string, string>> envForBox = seed.EnvVars;
                if (seed.Kind == ProjectKind.Codex
                    && seed.EnvVars.TryGetValue("OPENAI_API_KEY", out var key)
                    && !string.IsNullOrEmpty(key))
                {
                    ApiKeyBox.Password = key;
                    envForBox = seed.EnvVars.Where(kv => kv.Key != "OPENAI_API_KEY");
                }
                else if (seed.Kind == ProjectKind.Gemini
                    && seed.EnvVars.TryGetValue("GEMINI_API_KEY", out var gkey)
                    && !string.IsNullOrEmpty(gkey))
                {
                    ApiKeyBox.Password = gkey;
                    envForBox = seed.EnvVars.Where(kv => kv.Key != "GEMINI_API_KEY");
                }
                EnvBox.Text = string.Join(Environment.NewLine, envForBox.Select(kv => $"{kv.Key}={kv.Value}"));
            }
        }
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            UpdateTabColorSwatch();
            UpdateApiKeyRowVisibility();
        };
    }

    private void Kind_Checked(object sender, RoutedEventArgs e)
        => UpdateApiKeyRowVisibility();

    private void UpdateApiKeyRowVisibility()
    {
        if (ApiKeyRow is null) return;
        var isCodex = KindCodex.IsChecked == true;
        var isGemini = KindGemini.IsChecked == true;
        ApiKeyRow.Visibility = (isCodex || isGemini) ? Visibility.Visible : Visibility.Collapsed;
        if (ApiKeyLabel is not null)
            ApiKeyLabel.Text = isGemini ? "Gemini API key" : "OpenAI API key";
    }

    private void TabColorBox_TextChanged(object sender, TextChangedEventArgs e)
        => UpdateTabColorSwatch();

    private void TabColorPreset_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string hex }) TabColorBox.Text = hex;
    }

    private void TabColorClear_Click(object sender, RoutedEventArgs e)
        => TabColorBox.Text = string.Empty;

    private void TabColorPick_Click(object sender, RoutedEventArgs e)
    {
        using var dlg = new System.Windows.Forms.ColorDialog
        {
            AllowFullOpen = true,
            FullOpen = true,
            AnyColor = true,
            SolidColorOnly = true,
        };
        var text = TabColorBox.Text?.Trim();
        if (!string.IsNullOrEmpty(text))
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(text)!;
                dlg.Color = System.Drawing.Color.FromArgb(c.R, c.G, c.B);
            }
            catch { }
        }
        if (dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK)
        {
            var c = dlg.Color;
            TabColorBox.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
    }

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

        var kind = KindPwsh.IsChecked == true ? ProjectKind.Pwsh
                 : KindClaudeDirect.IsChecked == true ? ProjectKind.ClaudeDirect
                 : KindCodex.IsChecked == true ? ProjectKind.Codex
                 : KindGemini.IsChecked == true ? ProjectKind.Gemini
                 : ProjectKind.Claude;
        var command = string.IsNullOrWhiteSpace(CommandBox.Text) ? null : CommandBox.Text.Trim();
        var args = string.IsNullOrWhiteSpace(ArgsBox.Text) ? null : ArgsBox.Text.Trim();
        var env = ParseEnv(EnvBox.Text);
        if (kind == ProjectKind.Codex)
        {
            var apiKey = ApiKeyBox.Password;
            if (!string.IsNullOrEmpty(apiKey))
            {
                env ??= new Dictionary<string, string>(StringComparer.Ordinal);
                env["OPENAI_API_KEY"] = apiKey;
            }
        }
        else if (kind == ProjectKind.Gemini)
        {
            var apiKey = ApiKeyBox.Password;
            if (!string.IsNullOrEmpty(apiKey))
            {
                env ??= new Dictionary<string, string>(StringComparer.Ordinal);
                env["GEMINI_API_KEY"] = apiKey;
            }
        }
        var plansFolder = string.IsNullOrWhiteSpace(PlansFolderBox.Text) ? null : PlansFolderBox.Text.Trim();
        var tabColor = string.IsNullOrWhiteSpace(TabColorBox.Text) ? null : TabColorBox.Text.Trim();
        var launchCommand = string.IsNullOrWhiteSpace(LaunchCommandBox.Text) ? null : LaunchCommandBox.Text.Trim();
        var model = string.IsNullOrWhiteSpace(ModelBox.Text) ? null : ModelBox.Text.Trim();

        Result = new ProjectDefinition(name, path, kind, command, args, env, plansFolder, tabColor, launchCommand, model);
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
