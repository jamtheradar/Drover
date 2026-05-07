using System.Windows;
using Drover.App.Models;
using Drover.App.Services;

namespace Drover.App.Views;

/// <summary>
/// Modal task-details editor for <see cref="LocalFileRef"/> kanban cards. Title and
/// status are always editable; body is editable for heading-style cards
/// (<c>### [ ] foo</c>) only — list-item cards have no body slot. Sub-checklist and
/// gates are read-only here; toggle sub-items via the card's inline checkboxes.
///
/// Save writes through <see cref="LocalKanbanWriter"/>; the file watcher will
/// echo back and reload the kanban source.
/// </summary>
public partial class KanbanCardDetailsDialog : Window
{
    private readonly KanbanCard _card;
    private readonly LocalFileRef _ref;
    private readonly bool _isHeading;
    // Original raw line (with marker, gates marker, prefix) — used to detect/preserve
    // the trailing `<!-- gates: ... -->` annotation when the user edits the title.
    private readonly string _originalLine;
    private readonly string _originalBody;

    public KanbanCardDetailsDialog(KanbanCard card)
    {
        InitializeComponent();
        _card = card;
        if (card.SourceRef is not LocalFileRef lf)
            throw new System.ArgumentException("KanbanCardDetailsDialog only supports LocalFileRef cards.", nameof(card));
        _ref = lf;

        (_isHeading, _originalLine, _originalBody) = LoadSourceContext(lf.AbsolutePath, lf.LineNumber);

        HeaderText.Text = $"{lf.AbsolutePath} · line {lf.LineNumber}";
        TitleBox.Text = (card.Title ?? string.Empty).Trim();
        StatusBox.SelectedValue = card.Status;

        if (_isHeading)
        {
            BodyLabel.Visibility = Visibility.Visible;
            BodySection.Visibility = Visibility.Visible;
            BodyEditor.Text = _originalBody;
        }
        else
        {
            // Collapse the body row so the meta row (sub-checklist/gates) gets the slack.
            BodyRow.Height = System.Windows.GridLength.Auto;
            MetaRow.Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star);
        }

        if (card.SubChecklist.Count > 0)
        {
            SubChecklistSection.Visibility = Visibility.Visible;
            SubChecklistList.ItemsSource = card.SubChecklist;
        }

        if (card.Gates.Count > 0)
        {
            GatesSection.Visibility = Visibility.Visible;
            GatesList.ItemsSource = card.Gates;
        }

        Loaded += (_, _) => { TitleBox.Focus(); TitleBox.SelectAll(); };
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var newTitle = (TitleBox.Text ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(newTitle))
        {
            ShowError("Title cannot be empty.");
            return;
        }

        var newStatus = (KanbanStatus)(StatusBox.SelectedValue ?? KanbanStatus.Pending);
        char? newMarker = newStatus != _card.Status ? StatusToMarker(newStatus) : (char?)null;
        bool titleChanged = !string.Equals(newTitle, (_card.Title ?? string.Empty).Trim(), System.StringComparison.Ordinal);
        bool bodyChanged = _isHeading && !string.Equals(BodyEditor.Text ?? string.Empty, _originalBody, System.StringComparison.Ordinal);

        if (newMarker is null && !titleChanged && !bodyChanged)
        {
            DialogResult = true;
            return;
        }

        IsEnabled = false;
        try
        {
            if (newMarker is not null || titleChanged)
            {
                var titleArg = titleChanged ? newTitle : (string?)null;
                var ok = await LocalKanbanWriter.UpdateTaskLineAsync(_ref.AbsolutePath, _ref.LineNumber, newMarker, titleArg);
                if (!ok) { ShowError("Could not update the task line. The line may have moved or the file format changed."); return; }
            }

            if (bodyChanged)
            {
                var ok = await LocalKanbanWriter.ReplaceHeadingBodyAsync(_ref.AbsolutePath, _ref.LineNumber, BodyEditor.Text ?? string.Empty);
                if (!ok) { ShowError("Could not update the heading body."); return; }
            }

            DialogResult = true;
        }
        finally
        {
            IsEnabled = true;
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _ref.AbsolutePath,
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

    private static char StatusToMarker(KanbanStatus s) => s switch
    {
        KanbanStatus.Pending => ' ',
        KanbanStatus.Planned => '~',
        KanbanStatus.InProgress => '/',
        KanbanStatus.Completed => 'x',
        _ => ' ',
    };

    private static (bool IsHeading, string Line, string Body) LoadSourceContext(string path, int lineNumber)
    {
        try
        {
            var raw = System.IO.File.ReadAllText(path);
            var normalised = raw.Replace("\r\n", "\n").Replace("\r", "\n");
            var lines = normalised.Split('\n');
            if (lineNumber < 1 || lineNumber > lines.Length) return (false, string.Empty, string.Empty);
            var line = lines[lineNumber - 1];
            var isHeading = LocalKanbanWriter.IsHeadingTaskLine(line);
            if (!isHeading) return (false, line, string.Empty);

            // Body = lines after the heading until the next heading, HR, or EOF.
            // Skip code-fence content as part of the body (don't terminate on `#` inside fences).
            var body = new System.Text.StringBuilder();
            bool inFence = false;
            for (int i = lineNumber; i < lines.Length; i++)
            {
                var l = lines[i];
                var trimmed = l.TrimStart();
                if (!inFence)
                {
                    if (trimmed.StartsWith("```", System.StringComparison.Ordinal)) { inFence = true; AppendBodyLine(body, l); continue; }
                    if (l.StartsWith("#")) break;
                    if (l.Trim() == "---") break;
                }
                else
                {
                    if (trimmed.StartsWith("```", System.StringComparison.Ordinal)) { inFence = false; AppendBodyLine(body, l); continue; }
                }
                AppendBodyLine(body, l);
            }
            return (true, line, body.ToString().TrimEnd('\n'));
        }
        catch
        {
            return (false, string.Empty, string.Empty);
        }
    }

    private static void AppendBodyLine(System.Text.StringBuilder sb, string line)
    {
        if (sb.Length > 0) sb.Append('\n');
        sb.Append(line);
    }
}
