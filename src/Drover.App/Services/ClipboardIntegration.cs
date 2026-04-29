using System;
using System.Windows;
using System.Windows.Input;
using Drover.App.Terminal;
using MsTerm = Microsoft.Terminal.Wpf;

namespace Drover.App.Services;

/// <summary>
/// Wires Ctrl+C / Ctrl+V into the hosted terminal.
/// Ctrl+C: if the terminal has a selection, copy it to the clipboard and swallow the keystroke.
/// Otherwise let the 0x03 pass through so it still interrupts the foreground process as SIGINT
/// (crucial for stopping Claude mid-run). Hooked via the WriteInput intercept because the WPF
/// TerminalControl reliably emits 0x03 there.
/// Ctrl+V: with Win32InputMode on, the WPF TerminalControl encodes Ctrl+V as an extended Win32
/// key record and never emits a plain 0x16 byte through WriteInput, so the intercept can't see
/// it. Hook PreviewKeyDown on the control instead, read the clipboard, and write the text
/// directly to the PTY wrapped in bracketed-paste markers (ESC[200~ ... ESC[201~) so Claude
/// Code's TUI treats it as a single paste event rather than a stream of Enter presses.
/// </summary>
public sealed class ClipboardIntegration
{
    private const char Etx = ''; // Ctrl+C
    private const string PasteStart = "\x1b[200~";
    private const string PasteEnd = "\x1b[201~";

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
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // Ctrl+V or Shift+Insert: paste from clipboard.
        var isPaste = (ctrl && e.Key == Key.V) || (shift && e.Key == Key.Insert);
        if (!isPaste) return;

        var text = TryGetClipboard();
        if (string.IsNullOrEmpty(text))
        {
            e.Handled = true;
            return;
        }

        var normalized = text.Replace("\r\n", "\r").Replace("\n", "\r");
        try
        {
            _easy.Connection?.WriteToTerm(PasteStart + normalized + PasteEnd);
        }
        catch { /* PTY may be torn down — swallow */ }

        e.Handled = true;
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
            return true;
        }
        catch
        {
            return false;
        }
    }

    private string? TryGetClipboard()
    {
        try
        {
            return _easy.Dispatcher.Invoke(() => Clipboard.ContainsText() ? Clipboard.GetText() : null);
        }
        catch
        {
            return null;
        }
    }
}
