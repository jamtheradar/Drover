using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Terminal.Wpf;

namespace Drover.App.Terminal;

/// <summary>
/// WPF UserControl hosting <see cref="TerminalControl"/> and a
/// <see cref="ConPtyConnection"/>. Wires them together on first Loaded.
/// Replaces the old EasyTerminalControl wrapper with a tighter surface
/// tailored to Drover's needs.
///
/// Lifecycle:
///   1. ctor — construct the renderer, set AutoResize, hook Loaded.
///   2. renderer Loaded — apply theme (now that PresentationSource exists),
///      kick off the PTY on a worker thread.
///   3. PTY Ready (worker thread) — Dispatcher.Invoke to set
///      Terminal.Connection = pty, push the Win32 input mode escape into
///      the renderer, and resize. Synchronous so it lands before the read
///      loop starts pumping output.
///   4. read loop runs on the worker thread until the child exits.
///
/// The Win32 input mode escape (ESC[?9001h) is sent to the renderer rather
/// than the PTY so the renderer encodes keyboard input using extended
/// Win32 key records — required for Claude Code's TUI to receive
/// modifier-bearing keys (Shift+Enter etc.) faithfully.
/// </summary>
public class DroverTerminal : UserControl
{
    /// <summary>
    /// The hosted Microsoft.Terminal.Wpf renderer. Exposed so consumers can
    /// call <see cref="TerminalControl.GetSelectedText"/> directly.
    /// </summary>
    public TerminalControl Terminal { get; }

    /// <summary>
    /// The active PTY connection, or null if the control has not been
    /// loaded yet. Consumers (AttentionMonitor, SessionLogger,
    /// ClipboardIntegration, TerminalTabViewModel) reach into this for the
    /// intercept delegates and direct PTY writes.
    /// </summary>
    public ConPtyConnection? Connection { get; private set; }

    private TerminalDropHook? _dropHook;

    public string StartupCommandLine
    {
        get => (string)GetValue(StartupCommandLineProperty);
        set => SetValue(StartupCommandLineProperty, value);
    }
    public static readonly DependencyProperty StartupCommandLineProperty =
        DependencyProperty.Register(nameof(StartupCommandLine), typeof(string), typeof(DroverTerminal),
            new PropertyMetadata("powershell.exe"));

    public TerminalTheme? Theme
    {
        get => (TerminalTheme?)GetValue(ThemeProperty);
        set => SetValue(ThemeProperty, value);
    }
    public static readonly DependencyProperty ThemeProperty =
        DependencyProperty.Register(nameof(Theme), typeof(TerminalTheme?), typeof(DroverTerminal),
            new PropertyMetadata(null));

    public FontFamily FontFamilyWhenSettingTheme
    {
        get => (FontFamily)GetValue(FontFamilyWhenSettingThemeProperty);
        set => SetValue(FontFamilyWhenSettingThemeProperty, value);
    }
    public static readonly DependencyProperty FontFamilyWhenSettingThemeProperty =
        DependencyProperty.Register(nameof(FontFamilyWhenSettingTheme), typeof(FontFamily), typeof(DroverTerminal),
            new PropertyMetadata(new FontFamily("Cascadia Code")));

    public int FontSizeWhenSettingTheme
    {
        get => (int)GetValue(FontSizeWhenSettingThemeProperty);
        set => SetValue(FontSizeWhenSettingThemeProperty, value);
    }
    public static readonly DependencyProperty FontSizeWhenSettingThemeProperty =
        DependencyProperty.Register(nameof(FontSizeWhenSettingTheme), typeof(int), typeof(DroverTerminal),
            new PropertyMetadata(12));

    public DroverTerminal()
    {
        Terminal = new TerminalControl { AutoResize = true, Focusable = true };
        var grid = new Grid();
        grid.Children.Add(Terminal);
        Content = grid;
        Focusable = true;
        GotFocus += (_, _) => Terminal.Focus();
        Terminal.Loaded += OnTerminalLoaded;
    }

    private void OnTerminalLoaded(object sender, RoutedEventArgs e)
    {
        // Loaded fires again on reparent (PoppedOutWindow / DetachReattachProbe).
        // Don't re-create the PTY — keep the existing session alive.
        if (Connection is not null) return;

        if (Theme is { } theme)
            Terminal.SetTheme(theme, FontFamilyWhenSettingTheme.Source, (short)FontSizeWhenSettingTheme);

        var pty = new ConPtyConnection();
        Connection = pty;
        pty.Ready += OnPtyReady;

        var cmd = StartupCommandLine;
        var cols = Terminal.Columns;
        var rows = Terminal.Rows;
        Task.Run(() =>
        {
            try { pty.Start(cmd, cols, rows); }
            catch
            {
                // PTY init failed — leave the tab blank rather than crashing
                // the app. AttentionMonitor's "stale" timer will surface this
                // in the UI after 5s.
            }
        });
    }

    private void OnPtyReady(object? sender, EventArgs e)
    {
        // Synchronously hop to the UI thread so the renderer is subscribed
        // to TerminalOutput before ConPtyConnection.ReadLoop starts pumping.
        // This blocks the worker thread for one dispatcher tick — fine.
        Dispatcher.Invoke(() =>
        {
            if (Connection is not { } pty) return;
            Terminal.Connection = pty;
            pty.RaiseRendererOutput("\x1b[?9001h");
            pty.Resize(Terminal.Columns, Terminal.Rows);
            TryInstallDropHook();
        });
    }

    private void TryInstallDropHook()
    {
        if (_dropHook is not null) return;
        _dropHook = TerminalDropHook.TryInstall(Terminal, OnFilesDropped);
        if (_dropHook is null)
        {
            // HwndHost wasn't realized yet — retry on the next render frame.
            Dispatcher.BeginInvoke(new Action(TryInstallDropHook),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }
    }

    private void OnFilesDropped(string[] paths)
    {
        if (Connection is not { } pty || paths.Length == 0) return;

        // Quote each path; space-separate. No newline — let the user submit.
        // Bracketed paste markers prevent CC's TUI from interpreting embedded
        // characters as commands during the splat.
        var sb = new System.Text.StringBuilder();
        sb.Append("\x1b[200~");
        for (int i = 0; i < paths.Length; i++)
        {
            if (i > 0) sb.Append(' ');
            sb.Append('"').Append(paths[i]).Append('"');
        }
        sb.Append("\x1b[201~");
        pty.WriteToTerm(sb.ToString().AsSpan());
    }
}
