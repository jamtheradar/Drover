using System;
using System.Collections.Generic;
using System.Windows.Input;

namespace Drover.App.Services;

/// <summary>
/// Parses "Ctrl+Shift+K" style strings to KeyGesture. Used to drive customisable
/// keyboard shortcuts from settings. Tolerant: returns null on garbage rather than
/// throwing, so a bad config string doesn't break the whole shell.
/// Accepts WPF Key names ("OemComma") and the punctuation people actually type ("Ctrl+,").
/// </summary>
public static class GestureParser
{
    private static readonly Dictionary<string, Key> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [","] = Key.OemComma, ["."] = Key.OemPeriod, ["/"] = Key.OemQuestion,
        [";"] = Key.OemSemicolon, ["'"] = Key.OemQuotes, ["`"] = Key.Oem3,
        ["["] = Key.OemOpenBrackets, ["]"] = Key.OemCloseBrackets, ["\\"] = Key.OemPipe,
        ["-"] = Key.OemMinus, ["="] = Key.OemPlus,
        ["esc"] = Key.Escape, ["del"] = Key.Delete, ["ins"] = Key.Insert,
        ["pgup"] = Key.PageUp, ["pgdn"] = Key.PageDown,
    };

    private static readonly Dictionary<Key, string> DisplayNames = new()
    {
        [Key.OemComma] = ",", [Key.OemPeriod] = ".", [Key.OemQuestion] = "/",
        [Key.OemSemicolon] = ";", [Key.OemQuotes] = "'", [Key.Oem3] = "`",
        [Key.OemOpenBrackets] = "[", [Key.OemCloseBrackets] = "]", [Key.OemPipe] = "\\",
        [Key.OemMinus] = "-", [Key.OemPlus] = "=",
    };

    /// <summary>Claude Code keys a Drover shortcut must not shadow (Windows bindings).</summary>
    private static readonly Dictionary<string, string> ClaudeCodeKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ctrl+C"] = "interrupt", ["Ctrl+D"] = "exit", ["Ctrl+L"] = "clear screen",
        ["Ctrl+R"] = "history search", ["Ctrl+O"] = "transcript", ["Ctrl+T"] = "task list",
        ["Ctrl+B"] = "background task", ["Ctrl+G"] = "external editor", ["Ctrl+Z"] = "undo",
        ["Ctrl+J"] = "newline", ["Ctrl+K"] = "kill line", ["Ctrl+U"] = "kill to start",
        ["Ctrl+Y"] = "yank", ["Ctrl+A"] = "line start", ["Ctrl+E"] = "line end",
        ["Ctrl+W"] = "delete word", ["Ctrl+F"] = "forward char", ["Ctrl+V"] = "paste",
        ["Ctrl+X"] = "chord prefix", ["Ctrl+S"] = "stash prompt", ["Ctrl+P"] = "previous",
        ["Ctrl+N"] = "next",
        ["Alt+P"] = "switch model", ["Alt+T"] = "extended thinking", ["Alt+O"] = "fast mode",
        ["Alt+M"] = "permission mode", ["Alt+V"] = "paste image", ["Alt+B"] = "word back",
        ["Alt+F"] = "word forward", ["Alt+Enter"] = "newline", ["Shift+Enter"] = "newline",
        ["Shift+Tab"] = "cycle permission mode", ["Escape"] = "interrupt / rewind",
        ["Tab"] = "autocomplete",
    };

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
                    if (Aliases.TryGetValue(part, out var alias)) key = alias;
                    else if (part.Length == 1 && char.IsDigit(part[0])) key = Key.D0 + (part[0] - '0');
                    else if (Enum.TryParse<Key>(part, ignoreCase: true, out var k)) key = k;
                    else return null;
                    break;
            }
        }
        if (key is null) return null;
        try { return new KeyGesture(key.Value, mods); }
        catch { return null; }
    }

    /// <summary>Human-readable form, e.g. "Ctrl+OemComma" → "Ctrl+,".
    /// Returns the input unchanged when it doesn't parse.</summary>
    public static string Display(string? text)
    {
        var g = TryParse(text);
        if (g is null) return text ?? string.Empty;
        return Format(g);
    }

    /// <summary>If the gesture shadows a Claude Code key, a short description of
    /// what the user loses ("Alt+P — switch model"); otherwise null.</summary>
    public static string? ClaudeCodeConflict(string? text)
    {
        var g = TryParse(text);
        if (g is null) return null;
        var display = Format(g);
        return ClaudeCodeKeys.TryGetValue(display, out var what) ? $"{display} — Claude Code's {what}" : null;
    }

    private static string Format(KeyGesture g)
    {
        var s = "";
        if (g.Modifiers.HasFlag(ModifierKeys.Control)) s += "Ctrl+";
        if (g.Modifiers.HasFlag(ModifierKeys.Alt)) s += "Alt+";
        if (g.Modifiers.HasFlag(ModifierKeys.Shift)) s += "Shift+";
        if (g.Modifiers.HasFlag(ModifierKeys.Windows)) s += "Win+";
        string keyName;
        if (DisplayNames.TryGetValue(g.Key, out var n)) keyName = n;
        else if (g.Key >= Key.D0 && g.Key <= Key.D9) keyName = ((int)(g.Key - Key.D0)).ToString();
        else if (g.Key == Key.Return) keyName = "Enter";
        else keyName = g.Key.ToString();
        return s + keyName;
    }
}
