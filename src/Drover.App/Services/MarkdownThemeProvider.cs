using System.Windows;
using System.Windows.Media;

namespace Drover.App.Services;

/// <summary>
/// Pushes the active markdown theme into Application.Resources as
/// <c>MdTheme.*</c> brushes so non-AvalonEdit views (e.g. PlanPanel viewer)
/// can pick up the same palette via DynamicResource. Reacts to
/// SettingsStore.Changed so flipping the editor's theme repaints everywhere.
/// </summary>
public sealed class MarkdownThemeProvider
{
    private readonly SettingsStore _settings;

    public MarkdownTheme Current { get; private set; }
    public event EventHandler? Changed;

    public MarkdownThemeProvider(SettingsStore settings)
    {
        _settings = settings;
        Current = MarkdownThemes.Get(settings.Current.MarkdownTheme);
        Apply(Current);
        settings.Changed += OnSettingsChanged;
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        var next = MarkdownThemes.Get(_settings.Current.MarkdownTheme);
        if (next == Current) return;
        Current = next;
        Apply(next);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static void Apply(MarkdownTheme t)
    {
        var app = Application.Current;
        if (app is null) return;
        Set(app, "MdTheme.Background", t.Background);
        Set(app, "MdTheme.Foreground", t.Foreground);
        Set(app, "MdTheme.Muted", t.Quote);
        Set(app, "MdTheme.LineNumbers", t.LineNumbers);
        Set(app, "MdTheme.Selection", t.Selection);
        Set(app, "MdTheme.Heading", t.H1);
        Set(app, "MdTheme.SubHeading", t.H2);
        Set(app, "MdTheme.ListMark", t.ListMark);
        Set(app, "MdTheme.OrderedListMark", t.OrderedListMark ?? t.H1);
        Set(app, "MdTheme.Border", t.HR);
        Set(app, "MdTheme.TaskOpen", t.TaskOpen);
        Set(app, "MdTheme.TaskDone", t.TaskDone);
        Set(app, "MdTheme.Link", t.Link);
        Set(app, "MdTheme.InlineCodeFg", t.InlineCodeFg);
        Set(app, "MdTheme.InlineCodeBg", t.InlineCodeBg);
    }

    private static void Set(Application app, string key, string hex)
    {
        var c = (Color)ColorConverter.ConvertFromString(hex)!;
        var b = new SolidColorBrush(c);
        b.Freeze();
        app.Resources[key] = b;
    }
}
