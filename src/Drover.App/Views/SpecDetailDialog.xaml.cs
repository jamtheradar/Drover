using System.IO;
using System.Windows;
using System.Windows.Controls;
using Drover.App.Models;
using Drover.App.Services;

namespace Drover.App.Views;

/// <summary>
/// View / edit a single <see cref="SpecFile"/>. Title, status, block_severity
/// and the body are editable; everything else in the frontmatter (id, created,
/// estimate, depends_on, last_run_at, plus any unknown keys) is preserved
/// verbatim by <see cref="SpecFileWriter"/>.
/// </summary>
public partial class SpecDetailDialog : Window
{
    private readonly SpecFile _spec;
    private readonly string _originalBody;

    public SpecDetailDialog(SpecFile spec)
    {
        InitializeComponent();
        _spec = spec;
        _originalBody = ReadBody(spec.Path);

        var idLabel = string.IsNullOrEmpty(spec.Id) ? "(no id)" : spec.Id;
        HeaderText.Text = $"{spec.Path}  ·  id {idLabel}";
        TitleBox.Text = (spec.Title ?? string.Empty).Trim();
        StatusBox.SelectedValue = spec.Status == SpecStatus.Unknown ? SpecStatus.Pending : spec.Status;
        SeverityBox.SelectedValue = spec.BlockSeverity;
        BodyEditor.Text = _originalBody;
        UpdateSeverityVisibility();

        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    private void StatusBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSeverityVisibility();

    private void UpdateSeverityVisibility()
    {
        if (SeverityBox is null || SeverityLabel is null) return;
        var isBlocked = (StatusBox.SelectedValue as SpecStatus?) == SpecStatus.Blocked;
        SeverityBox.Visibility = isBlocked ? Visibility.Visible : Visibility.Collapsed;
        SeverityLabel.Visibility = isBlocked ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var newTitle = (TitleBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newTitle))
        {
            ShowError("Title cannot be empty.");
            return;
        }

        var status = (SpecStatus)(StatusBox.SelectedValue ?? SpecStatus.Pending);
        var severity = (BlockSeverity)(SeverityBox.SelectedValue ?? BlockSeverity.None);

        if (!SpecFileWriter.TryUpdate(_spec.Path, newTitle, status, severity, BodyEditor.Text ?? string.Empty, out var error))
        {
            ShowError($"Could not save spec: {error}");
            return;
        }

        DialogResult = true;
    }

    private void FontDown_Click(object sender, RoutedEventArgs e) => AdjustBodyScale(-0.1);
    private void FontUp_Click(object sender, RoutedEventArgs e) => AdjustBodyScale(+0.1);
    private void FontReset_Click(object sender, RoutedEventArgs e) => SetBodyScale(1.0);

    private void AdjustBodyScale(double delta)
        => SetBodyScale(System.Math.Round((BodyScale.ScaleX + delta) * 100) / 100);

    private void SetBodyScale(double scale)
    {
        scale = System.Math.Clamp(scale, 0.7, 2.5);
        BodyScale.ScaleX = scale;
        BodyScale.ScaleY = scale;
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _spec.Path,
                UseShellExecute = true,
            });
        }
        catch { /* best-effort */ }
    }

    private void ShowError(string text)
    {
        ErrorText.Text = text;
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Body = everything after the closing `---` of the frontmatter block.
    /// Returns empty string if the file is unreadable or missing frontmatter
    /// (the writer rejects those cases too, so we won't accidentally truncate).
    /// </summary>
    private static string ReadBody(string path)
    {
        try
        {
            var raw = File.ReadAllText(path);
            var normalised = raw.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalised.Split('\n');
            if (lines.Length == 0 || lines[0].TrimEnd() != "---") return string.Empty;
            var fmEnd = -1;
            for (var i = 1; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd() == "---") { fmEnd = i; break; }
            }
            if (fmEnd < 0) return string.Empty;
            var bodyStart = fmEnd + 1;
            // Drop a single leading blank line if present — the writer re-emits one.
            if (bodyStart < lines.Length && lines[bodyStart].Length == 0) bodyStart++;
            if (bodyStart >= lines.Length) return string.Empty;
            var body = string.Join('\n', lines, bodyStart, lines.Length - bodyStart);
            return body.TrimEnd('\n');
        }
        catch
        {
            return string.Empty;
        }
    }
}
