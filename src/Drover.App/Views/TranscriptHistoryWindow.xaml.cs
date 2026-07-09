using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Drover.App.Services;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace Drover.App.Views;

/// <summary>
/// Browser for the per-session <c>.transcript.md</c> files that
/// <see cref="TranscriptCapture"/> writes under %APPDATA%\Drover\logs. Left pane
/// lists sessions newest-first; right pane renders the selected transcript via
/// <see cref="MarkdownRenderer"/>. Typing in the search box greps every transcript
/// on a background thread (the whole corpus is small — retention caps it), filters
/// the list to matching sessions, and highlights hits in the open document.
///
/// Distinct from <see cref="GlobalFindWindow"/>, which searches the raw VT traces
/// that only exist at the Full log level and opens hits externally — this window
/// covers the default Transcript level and reads in-place.
/// </summary>
public partial class TranscriptHistoryWindow : Window
{
    /// <summary>One transcript on disk. MatchCount is 0 outside an active search.</summary>
    public sealed record Entry(string Path, string Title, string MetaLine, System.DateTime SortKey, int MatchCount, string Project)
    {
        public string MatchLabel => MatchCount switch
        {
            0 => string.Empty,
            1 => "1 match",
            _ => $"{MatchCount} matches",
        };
    }

    /// <summary>{safeTitle}_{yyyyMMdd}_{HHmmss}.transcript.md — see SessionLogger's stamp format.</summary>
    private static readonly Regex FileStamp = new(
        @"^(?<title>.+)_(?<date>\d{8})_(?<time>\d{6})$",
        RegexOptions.Compiled);

    /// <summary>Preamble comment TranscriptCapture writes as the file's first line.
    /// The project segment is optional — transcripts written before it existed lack it.</summary>
    private static readonly Regex Preamble = new(
        @"agent: (?<agent>.+?) — model: (?<model>.+?)(?: — project: (?<project>.+?))? — started",
        RegexOptions.Compiled);

    private static readonly Brush MatchBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x55, 0x00, 0x7a, 0xcc)));
    private static readonly Brush CurrentMatchBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xAA, 0x00, 0x7a, 0xcc)));
    private static readonly Brush MarkBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB4, 0x3d, 0x9e, 0xf2)));
    private static readonly Brush CurrentMarkBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xFF, 0xbf, 0xe3, 0xff)));

    private static Brush Frozen(Brush b) { b.Freeze(); return b; }

    private static string LogsDir => System.IO.Path.Combine(
        System.Environment.GetFolderPath(System.Environment.SpecialFolder.ApplicationData), "Drover", "logs");

    private const string AllProjectsLabel = "All projects";

    private List<Entry> _all = new();
    private string _activeQuery = string.Empty;
    private Dictionary<string, int>? _lastCounts;
    private string? _selectedPath;
    private bool _suppressSelectionRender;
    private bool _suppressProjectFilter;
    private CancellationTokenSource? _cts;

    private readonly List<TextRange> _matchRanges = new();
    private readonly List<double> _matchFractions = new();
    private int _matchIndex = -1;
    private ScrollViewer? _viewerScroll;

    public TranscriptHistoryWindow()
    {
        InitializeComponent();
        LoadEntries();
        ApplyFilter(null);
        Loaded += (_, _) =>
        {
            SearchBox.Focus();
            HookViewerScroll();
        };
    }

    /// <summary>
    /// Wires layout listeners once the FlowDocumentScrollViewer's template exists.
    /// Bottomless FlowDocument layout grows the scroll extent in the background,
    /// so the scrollbar ticks re-place themselves whenever the geometry settles.
    /// </summary>
    private void HookViewerScroll()
    {
        _viewerScroll ??= FindDescendant<ScrollViewer>(Viewer);
        if (_viewerScroll != null)
            _viewerScroll.ScrollChanged += (_, e) =>
            {
                if (e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
                    UpdateMatchMarks();
            };
        Viewer.SizeChanged += (_, _) => UpdateMatchMarks();
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T hit) return hit;
            if (FindDescendant<T>(child) is { } inner) return inner;
        }
        return null;
    }

    private void LoadEntries()
    {
        var list = new List<Entry>();
        try
        {
            Directory.CreateDirectory(LogsDir);
            foreach (var path in Directory.EnumerateFiles(LogsDir, "*.transcript.md"))
            {
                var fi = new FileInfo(path);
                // FileName minus the double extension ".transcript.md".
                var stem = fi.Name[..^".transcript.md".Length];

                var title = stem;
                var stamp = fi.LastWriteTime;
                var m = FileStamp.Match(stem);
                if (m.Success)
                {
                    title = m.Groups["title"].Value;
                    if (System.DateTime.TryParseExact(
                            m.Groups["date"].Value + m.Groups["time"].Value, "yyyyMMddHHmmss",
                            null, System.Globalization.DateTimeStyles.None, out var parsed))
                        stamp = parsed;
                }

                var meta = new StringBuilder();
                meta.Append(stamp.ToString("yyyy-MM-dd HH:mm"));
                var (agent, model, project) = ReadPreamble(path);
                if (!string.IsNullOrEmpty(agent)) meta.Append(" · ").Append(agent);
                if (!string.IsNullOrEmpty(model)) meta.Append(" · ").Append(model);
                meta.Append(" · ").Append(SizeLabel(fi.Length));

                // Pre-project-preamble transcripts: the filename title is the tab title,
                // which defaults to the project name — close enough for grouping.
                if (string.IsNullOrEmpty(project)) project = title;

                list.Add(new Entry(path, title, meta.ToString(), stamp, 0, project));
            }
        }
        catch (System.Exception ex)
        {
            AppLog.Warn("TranscriptHistory", $"failed to enumerate {LogsDir}: {ex.Message}");
        }

        _all = list.OrderByDescending(e => e.SortKey).ToList();
        SubtitleText.Text = $"{_all.Count} transcript(s) in {LogsDir}";
        PopulateProjectFilter();
    }

    /// <summary>Rebuilds the project dropdown from the loaded entries, keeping the
    /// current selection when that project still exists after a refresh.</summary>
    private void PopulateProjectFilter()
    {
        var previous = SelectedProject;
        var projects = _all.Select(e => e.Project)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(System.StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => p, System.StringComparer.OrdinalIgnoreCase)
            .ToList();

        _suppressProjectFilter = true;
        ProjectFilter.Items.Clear();
        ProjectFilter.Items.Add(AllProjectsLabel);
        foreach (var p in projects) ProjectFilter.Items.Add(p);
        ProjectFilter.SelectedIndex = previous is null
            ? 0
            : System.Math.Max(0, projects.FindIndex(p => string.Equals(p, previous, System.StringComparison.OrdinalIgnoreCase)) + 1);
        _suppressProjectFilter = false;
    }

    /// <summary>Currently selected project, or null when "All projects".</summary>
    private string? SelectedProject
        => ProjectFilter.SelectedIndex <= 0 ? null : ProjectFilter.SelectedItem as string;

    /// <summary>Agent/model/project from the first-line preamble comment; empty strings when absent or "—".</summary>
    private static (string Agent, string Model, string Project) ReadPreamble(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            var first = sr.ReadLine() ?? string.Empty;
            var m = Preamble.Match(first);
            if (!m.Success) return (string.Empty, string.Empty, string.Empty);
            var agent = m.Groups["agent"].Value.Trim();
            var model = m.Groups["model"].Value.Trim();
            var project = m.Groups["project"].Value.Trim();
            if (agent == "—") agent = string.Empty;
            if (model == "—") model = string.Empty;
            if (project == "—") project = string.Empty;
            return (agent, model, project);
        }
        catch
        {
            return (string.Empty, string.Empty, string.Empty);
        }
    }

    /// <summary>
    /// Drops the first-line preamble comment before display/search — MarkdownRenderer
    /// has no HTML-comment handling and would print it literally, and a hit inside it
    /// would inflate the search count past what the viewer highlights. The list row
    /// already surfaces the same agent/model/date.
    /// </summary>
    private static string StripPreamble(string text)
    {
        if (!text.StartsWith("<!--", System.StringComparison.Ordinal)) return text;
        var nl = text.IndexOf('\n');
        return nl < 0 ? string.Empty : text[(nl + 1)..].TrimStart('\r', '\n');
    }

    private static string SizeLabel(long bytes)
        => bytes < 1024 ? $"{bytes} B"
         : bytes < 1024 * 1024 ? $"{bytes / 1024.0:0.#} KB"
         : $"{bytes / (1024.0 * 1024.0):0.#} MB";

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadEntries();
        var q = SearchBox.Text.Trim();
        if (q.Length == 0) ApplyFilter(null);
        else StartSearch(q);
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _cts?.Cancel();
        var q = SearchBox.Text.Trim();
        if (q.Length == 0)
        {
            ApplyFilter(null);
            return;
        }
        StartSearch(q);
    }

    private void StartSearch(string query)
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        _ = RunSearch(query, _cts.Token);
    }

    private async Task RunSearch(string query, CancellationToken ct)
    {
        StatusLine.Text = "searching…";
        var paths = _all.Select(e => e.Path).ToList();

        var counts = await Task.Run(() =>
        {
            var dict = new Dictionary<string, int>(System.StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
            {
                if (ct.IsCancellationRequested) break;
                string text;
                try
                {
                    // The live session's writer keeps the file open — share broadly.
                    using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs, Encoding.UTF8);
                    text = StripPreamble(sr.ReadToEnd());
                }
                catch { continue; }

                int count = 0, idx = 0;
                while ((idx = text.IndexOf(query, idx, System.StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    count++;
                    idx += query.Length;
                }
                if (count > 0) dict[path] = count;
            }
            return dict;
        }, ct);

        if (ct.IsCancellationRequested) return;
        ApplyFilter(counts, query);
    }

    /// <summary>
    /// Rebuilds the session list. counts=null clears the search (all sessions shown);
    /// otherwise only sessions with hits survive, stamped with their match count.
    /// The project dropdown further narrows both states. Selection is preserved by
    /// path where possible, then the document re-renders once with the new
    /// highlight state.
    /// </summary>
    private void ApplyFilter(Dictionary<string, int>? counts, string query = "")
    {
        _activeQuery = counts is null ? string.Empty : query;
        _lastCounts = counts;

        var project = SelectedProject;
        var pool = project is null
            ? _all
            : _all.Where(e => string.Equals(e.Project, project, System.StringComparison.OrdinalIgnoreCase)).ToList();

        List<Entry> shown = counts is null
            ? pool
            : pool.Where(e => counts.ContainsKey(e.Path))
                  .Select(e => e with { MatchCount = counts[e.Path] })
                  .ToList();

        _suppressSelectionRender = true;
        Sessions.ItemsSource = shown;
        var selected = shown.FirstOrDefault(e => string.Equals(e.Path, _selectedPath, System.StringComparison.OrdinalIgnoreCase))
                       ?? shown.FirstOrDefault();
        Sessions.SelectedItem = selected;
        _suppressSelectionRender = false;

        RenderSelected();

        var scope = project is null ? "session(s)" : $"session(s) in {project}";
        if (counts is null)
        {
            StatusLine.Text = _all.Count == 0
                ? "No transcripts yet — sessions write one automatically unless logging is Off."
                : $"{pool.Count} {scope} · type to search all transcripts";
        }
        else
        {
            var total = shown.Sum(e => e.MatchCount);
            StatusLine.Text = total == 0
                ? $"No matches for \"{query}\"{(project is null ? "" : $" in {project}")} · Esc to clear"
                : $"{total:N0} match(es) in {shown.Count} of {pool.Count} {scope} · Enter/F3 next · Shift+F3 previous";
        }
    }

    private void ProjectFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressProjectFilter) return;
        ApplyFilter(_lastCounts, _activeQuery);
    }

    private void Sessions_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelectionRender) return;
        RenderSelected();
    }

    private void RenderSelected()
    {
        _matchRanges.Clear();
        _matchFractions.Clear();
        _matchIndex = -1;
        MatchMarks.Children.Clear();

        if (Sessions.SelectedItem is not Entry entry)
        {
            _selectedPath = null;
            Viewer.Document = null;
            return;
        }
        _selectedPath = entry.Path;

        string markdown;
        try
        {
            using var fs = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            markdown = sr.ReadToEnd();
        }
        catch (System.Exception ex)
        {
            markdown = $"# {entry.Title}\n\nFailed to read transcript: {ex.Message}";
        }

        Viewer.Document = MarkdownRenderer.Render(StripPreamble(markdown));

        if (_activeQuery.Length > 0)
        {
            HighlightMatches(Viewer.Document, _activeQuery);
            if (_matchRanges.Count > 0) JumpToMatch(0);
        }

        // The scrollbar doesn't have its final geometry until the new document
        // has been through a layout pass — place the ticks after it.
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new System.Action(UpdateMatchMarks));
    }

    /// <summary>
    /// Finds and paints every occurrence of the query in the rendered document.
    /// Scans run-by-run, so a match that straddles a formatting boundary (half
    /// bold, half plain) is missed — the file-level count can therefore exceed
    /// the number of painted ranges. Good enough for eyeballing history.
    /// </summary>
    private void HighlightMatches(FlowDocument doc, string query)
    {
        var offsets = new List<int>();
        var running = 0;
        var pointer = doc.ContentStart;
        while (pointer != null)
        {
            if (pointer.GetPointerContext(LogicalDirection.Forward) == TextPointerContext.Text)
            {
                var text = pointer.GetTextInRun(LogicalDirection.Forward);
                var idx = 0;
                while ((idx = text.IndexOf(query, idx, System.StringComparison.OrdinalIgnoreCase)) >= 0)
                {
                    var start = pointer.GetPositionAtOffset(idx);
                    var end = pointer.GetPositionAtOffset(idx + query.Length);
                    if (start != null && end != null)
                    {
                        _matchRanges.Add(new TextRange(start, end));
                        offsets.Add(running + idx);
                    }
                    idx += query.Length;
                }
                running += text.Length;
            }
            pointer = pointer.GetNextContextPosition(LogicalDirection.Forward);
        }

        // Character offset as a fraction of total text — the tick-position fallback
        // for matches FlowDocument's background layout hasn't reached yet.
        if (running > 0)
            foreach (var o in offsets)
                _matchFractions.Add((double)o / running);

        foreach (var r in _matchRanges)
            r.ApplyPropertyValue(TextElement.BackgroundProperty, MatchBrush);
    }

    /// <summary>
    /// Repaints the match ticks over the viewer's vertical scrollbar. Each tick sits
    /// at its match's real laid-out position once FlowDocument's background layout
    /// has reached it — the ScrollChanged hook re-runs this as the extent grows, so
    /// the character-offset estimate only stands in for the not-yet-formatted tail.
    /// Ticks are placed along the scrollbar's track, not the full bar, so they line
    /// up with where the thumb travels. Overlapping ticks are collapsed so a
    /// one-letter query can't flood the canvas; the current match paints last, on top.
    /// </summary>
    private void UpdateMatchMarks()
    {
        MatchMarks.Children.Clear();
        if (_matchFractions.Count == 0) return;

        _viewerScroll ??= FindDescendant<ScrollViewer>(Viewer);
        if (_viewerScroll?.Template?.FindName("PART_VerticalScrollBar", _viewerScroll) is not ScrollBar bar) return;
        if (!bar.IsVisible || bar.ActualHeight <= 0) return;
        if (_viewerScroll.ScrollableHeight <= 0) return; // everything fits — highlights are already on screen

        // The thumb travels the track, not the whole bar — excluding the arrow
        // buttons keeps a tick level with the thumb when its match is in view.
        FrameworkElement region = bar.Track is { ActualHeight: > 0 } track ? track : bar;
        var origin = region.TransformToAncestor(ViewerHost).Transform(new Point(0, 0));
        const double markHeight = 3.0;
        var width = System.Math.Max(4.0, bar.ActualWidth - 6.0);
        var left = origin.X + (region.ActualWidth - width) / 2.0;
        var usable = System.Math.Max(0.0, region.ActualHeight - markHeight);

        void Add(double fraction, Brush fill)
        {
            var mark = new Rectangle { Width = width, Height = markHeight, RadiusX = 1, RadiusY = 1, Fill = fill };
            Canvas.SetLeft(mark, left);
            Canvas.SetTop(mark, origin.Y + fraction * usable);
            MatchMarks.Children.Add(mark);
        }

        var extent = _viewerScroll.ExtentHeight;
        var offset = _viewerScroll.VerticalOffset;

        double FractionFor(int i)
        {
            // GetCharacterRect is viewport-relative; adding VerticalOffset gives
            // document coordinates. HasValidLayout gates the call so a match in
            // unformatted tail content never forces a synchronous full layout.
            var start = _matchRanges[i].Start;
            if (extent > 0 && start.HasValidLayout)
            {
                var rect = start.GetCharacterRect(LogicalDirection.Forward);
                if (!rect.IsEmpty)
                    return System.Math.Clamp((rect.Top + offset) / extent, 0.0, 1.0);
            }
            return _matchFractions[i];
        }

        var seen = new HashSet<int>();
        double? current = null;
        for (var i = 0; i < _matchFractions.Count; i++)
        {
            var fraction = FractionFor(i);
            if (i == _matchIndex) { current = fraction; continue; }
            if (!seen.Add((int)System.Math.Round(fraction * usable))) continue;
            Add(fraction, MarkBrush);
        }
        if (current is { } c)
            Add(c, CurrentMarkBrush);
    }

    private void JumpToMatch(int index)
    {
        if (_matchRanges.Count == 0) return;

        // Restore the previous current-match back to the normal highlight.
        if (_matchIndex >= 0 && _matchIndex < _matchRanges.Count)
            _matchRanges[_matchIndex].ApplyPropertyValue(TextElement.BackgroundProperty, MatchBrush);

        _matchIndex = ((index % _matchRanges.Count) + _matchRanges.Count) % _matchRanges.Count;
        var range = _matchRanges[_matchIndex];
        range.ApplyPropertyValue(TextElement.BackgroundProperty, CurrentMatchBrush);
        (range.Start.Parent as FrameworkContentElement)?.BringIntoView();

        UpdateMatchMarks();
        StatusLine.Text = $"match {_matchIndex + 1} of {_matchRanges.Count} in this session · Enter/F3 next · Shift+F3 previous";
    }

    private void Sessions_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Sessions.SelectedItem is not Entry entry) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(entry.Path) { UseShellExecute = true });
        }
        catch
        {
            StatusLine.Text = "open failed — no app registered for .md?";
        }
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                if (!string.IsNullOrEmpty(SearchBox.Text))
                {
                    SearchBox.Text = string.Empty;
                }
                else
                {
                    Close();
                }
                e.Handled = true;
                break;
            case Key.Enter:
                JumpToMatch(_matchIndex + 1);
                e.Handled = true;
                break;
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
        if (e.Key == Key.F3)
        {
            var step = (Keyboard.Modifiers & ModifierKeys.Shift) != 0 ? -1 : +1;
            JumpToMatch(_matchIndex + step);
            e.Handled = true;
            return;
        }
        base.OnPreviewKeyDown(e);
    }
}
