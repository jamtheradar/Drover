using System;
using System.Windows.Input;

namespace Drover.App.Services;

/// <summary>
/// Parses "Ctrl+Shift+K" style strings to KeyGesture. Used to drive customisable
/// keyboard shortcuts from settings. Tolerant: returns null on garbage rather than
/// throwing, so a bad config string doesn't break the whole shell.
/// </summary>
public static class GestureParser
{
    public static KeyGesture? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = ModifierKeys.None;
        Key? key = null;
        foreach (var part in parts)
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModifierKeys.Control; break;
                case "shift": mods |= ModifierKeys.Shift; break;
                case "alt": mods |= ModifierKeys.Alt; break;
                case "win": case "windows": mods |= ModifierKeys.Windows; break;
                default:
                    if (Enum.TryParse<Key>(part, ignoreCase: true, out var k)) key = k;
                    else return null;
                    break;
            }
        }
        if (key is null) return null;
        try { return new KeyGesture(key.Value, mods); }
        catch { return null; }
    }
}
