using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Drover.App.Terminal;
using MsTerm = Microsoft.Terminal.Wpf;

namespace Drover.App.Services;

/// <summary>
/// Wires copy / paste into the hosted terminal.
/// With Win32InputMode on (DroverTerminal injects ESC[?9001h), the WPF TerminalControl encodes
/// every keystroke as an extended Win32 key record — plain 0x03/0x16 bytes never reach the
/// WriteInput intercept, so both copy and paste are hooked via PreviewKeyDown instead.
/// Ctrl+C / Ctrl+Shift+C / Ctrl+Insert: if the terminal has a selection, copy it, clear the
/// selection and mark the event handled. Without a selection, plain Ctrl+C falls through so it
/// still interrupts the foreground process (crucial for stopping Claude mid-run).
/// Ctrl+V / Ctrl+Shift+V / Shift+Insert:
///   - text  → written to the PTY wrapped in bracketed-paste markers (ESC[200~ ... ESC[201~)
///             so Claude Code's TUI treats it as one paste rather than a stream of Enters.
///   - image → saved as a PNG under %TEMP%\Drover\pastes and its quoted path pasted; Claude
///             Code attaches image paths it sees in the prompt. (Claude can't read the
///             clipboard itself here because the keystroke never reaches it.)
///   - files copied in Explorer → their quoted paths pasted, same as drag-drop.
/// The legacy 0x03 WriteInput intercept is kept as a fallback for input paths that bypass
/// PreviewKeyDown or sessions without Win32 input mode.
/// </summary>
public sealed class ClipboardIntegration
{
    private const char Etx = '\x03'; // Ctrl+C
    private const string PasteStart = "\x1b[200~";
    private const string PasteEnd = "\x1b[201~";
    private static readonly TimeSpan PasteRetention = TimeSpan.FromDays(7);

    private readonly DroverTerminal _easy;

    public ClipboardIntegration(DroverTerminal easy)
    {
        _easy = easy;
    }

    public void Attach()
    {
        if (_easy.Connection is null) return;

        var previous = _easy.Connection.InterceptInputToTermApp;
        _easy.Connection.InterceptInputToTermApp = (ref Span<char> span) =>
        {
            if (span.Length == 1 && span[0] == Etx)
            {
                if (TryCopySelection()) span = Span<char>.Empty;
            }
            previous?.Invoke(ref span);
        };

        _easy.PreviewKeyDown += OnPreviewKeyDown;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var mods = Keyboard.Modifiers;
        var ctrl = (mods & ModifierKeys.Control) != 0;
        var shift = (mods & ModifierKeys.Shift) != 0;
        var alt = (mods & ModifierKeys.Alt) != 0;
        if (alt) return;

        // Ctrl+C / Ctrl+Shift+C / Ctrl+Insert with a selection: copy and swallow.
        // Ctrl+Shift+C is swallowed even without a selection — Windows Terminal
        // users expect it to be copy, never an interrupt.
        var isCopy = ctrl && (e.Key == Key.C || e.Key == Key.Insert);
        if (isCopy)
        {
            if (TryCopySelection())
            {
                e.Handled = true;
                return;
            }
            if (shift && e.Key == Key.C) e.Handled = true;
            return;
        }

        var isPaste = (ctrl && e.Key == Key.V) || (shift && !ctrl && e.Key == Key.Insert);
        if (!isPaste) return;

        if (TryPasteFromClipboard()) e.Handled = true;
    }

    /// <summary>Pastes whatever the clipboard holds. Returns false when there was
    /// nothing usable, so the keystroke falls through to the session.</summary>
    private bool TryPasteFromClipboard()
    {
        if (_easy.Connection is null) return false;

        string? text = null;
        string[]? files = null;
        BitmapSource? image = null;
        try
        {
            _easy.Dispatcher.Invoke(() =>
            {
                if (Clipboard.ContainsText()) text = Clipboard.GetText();
                else if (Clipboard.ContainsFileDropList())
                    files = Clipboard.GetFileDropList().Cast<string>().ToArray();
                else if (Clipboard.ContainsImage()) image = Clipboard.GetImage();
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn("Clipboard", $"Clipboard read failed: {ex.Message}");
            return false;
        }

        if (!string.IsNullOrEmpty(text))
        {
            var normalized = text.Replace("\r\n", "\r").Replace("\n", "\r");
            try { _easy.Connection?.WriteToTerm(PasteStart + normalized + PasteEnd); }
            catch { /* PTY may be torn down — swallow */ }
            return true;
        }

        if (files is { Length: > 0 })
        {
            _easy.PastePaths(files);
            return true;
        }

        if (image is not null && SaveImage(image) is { } path)
        {
            _easy.PastePaths(new[] { path });
            return true;
        }

        return false;
    }

    private static string? SaveImage(BitmapSource image)
    {
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "Drover", "pastes");
            Directory.CreateDirectory(dir);
            PruneOldPastes(dir);

            var path = Path.Combine(dir, $"paste-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            using (var fs = File.Create(path)) encoder.Save(fs);
            return path;
        }
        catch (Exception ex)
        {
            AppLog.Warn("Clipboard", $"Saving pasted image failed: {ex.Message}");
            return null;
        }
    }

    private static void PruneOldPastes(string dir)
    {
        try
        {
            var cutoff = DateTime.Now - PasteRetention;
            foreach (var f in Directory.EnumerateFiles(dir, "paste-*.png"))
                if (File.GetLastWriteTime(f) < cutoff) File.Delete(f);
        }
        catch { /* best effort */ }
    }

    private bool TryCopySelection()
    {
        string? selected = null;
        try
        {
            var terminal = _easy.Terminal;
            selected = terminal.Dispatcher.Invoke(() => terminal.GetSelectedText());
        }
        catch { return false; }

        if (string.IsNullOrEmpty(selected)) return false;

        try
        {
            _easy.Dispatcher.Invoke(() => Clipboard.SetText(selected));
        }
        catch
        {
            return false;
        }

        // Clear the selection like Windows Terminal does, so the next Ctrl+C
        // interrupts instead of copying the same text again.
        ClearSelection(_easy.Terminal);
        return true;
    }

    // TerminalControl doesn't expose ClearSelection publicly; the native export
    // takes the renderer handle held privately on its TerminalContainer.
    private static readonly FieldInfo? ContainerField =
        typeof(MsTerm.TerminalControl).GetField("termContainer", BindingFlags.NonPublic | BindingFlags.Instance);
    private static FieldInfo? _terminalHandleField;

    [DllImport("Microsoft.Terminal.Control.dll", CallingConvention = CallingConvention.StdCall)]
    private static extern void TerminalClearSelection(IntPtr terminal);

    private static void ClearSelection(MsTerm.TerminalControl control)
    {
        try
        {
            var container = ContainerField?.GetValue(control);
            if (container is null) return;
            _terminalHandleField ??= container.GetType()
                .GetField("terminal", BindingFlags.NonPublic | BindingFlags.Instance);
            if (_terminalHandleField?.GetValue(container) is IntPtr h && h != IntPtr.Zero)
                control.Dispatcher.Invoke(() => TerminalClearSelection(h));
        }
        catch { /* best effort — worst case the selection stays visible */ }
    }
}
