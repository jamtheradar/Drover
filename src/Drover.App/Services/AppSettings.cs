using System.Collections.Generic;

namespace Drover.App.Services;

public sealed record WindowPlacement(
    double Left,
    double Top,
    double Width,
    double Height,
    bool Maximized);

public sealed record AppSettings(
    string FontFamily = "Cascadia Code",
    int FontSize = 12,
    WindowPlacement? Window = null,
    bool ResumeOnRestore = false,
    KeyboardShortcuts? Shortcuts = null,
    // Approximate USD spend that fills the top-bar session bar. Claude Code's actual
    // 5-hour usage cap is plan-specific and only known server-side, so this is a local proxy.
    double SessionBudgetUsd = 10.0,
    // When true, Drover claims Claude Code's statusLine slot in ~/.claude/settings.json
    // so it can receive CC's rich StatusJSON push (context %, rate limits, cost, etc.).
    // Off by default — clobbering an existing entry (e.g. ccstatusline) is destructive.
    // The previous statusLine block is backed up to %APPDATA%\Drover\statusline-backup.json
    // and restored when this is turned back off.
    bool TakeOverStatusLine = false,
    string MarkdownTheme = "Drover Dark",
    // Master switch for the Claude Code hooks integration. When false, Drover removes
    // its hook entries from ~/.claude/settings.json so CC stops calling the forwarder,
    // and statusLine takeover is also disabled regardless of TakeOverStatusLine.
    // The loopback gateway stays running so toggling back on is cheap.
    bool HooksEnabled = true,
    // When true, the gateway writes every hook event and statusLine push to
    // %APPDATA%\Drover\logs\hooks.jsonl / statusline.jsonl (5MB rotation). Off by
    // default — only useful for diagnosing what CC is sending.
    bool HooksDebugLogging = false,
    // Controls whether the Stop event (assistant turn finished, ready for next user
    // input) is wired into CC's settings.json. This is the signal that drives the
    // tab's "idle" attention state. On by default.
    bool IdleHookEnabled = true,
    // Last app version for which the user dismissed the What's New dialog. Empty
    // means they've never seen it; on startup we compare against AppInfo.Version
    // and pop the dialog once when they differ.
    string LastSeenVersion = "",
    // Override for the `claude` executable used to launch Claude tabs. When set,
    // replaces the bare "claude" command in the pwsh wrapper. Leave blank to rely
    // on PATH resolution. Per-project Command still takes precedence.
    string ClaudeExecutablePath = "",
    // Default --model flag appended to Claude tabs that don't carry a per-project
    // override. Empty = let Claude pick its default. Per-project Args/Model wins.
    string DefaultClaudeModel = "",
    // KEY=VALUE pairs injected into every Claude/pwsh launch ahead of per-project
    // env. Useful for ANTHROPIC_API_KEY, NO_COLOR, proxy vars, etc.
    Dictionary<string, string>? GlobalEnvVars = null,
    // Session-log retention. SessionLogger prunes oldest files when either limit
    // is exceeded. 0 = no count cap; 0 MB = no byte cap.
    int LogKeepCount = 50,
    int LogByteBudgetMb = 500,
    // When true, registers a HKCU\...\Run entry pointing at this exe so Drover
    // launches at sign-in. The toggle writes the registry on Save.
    bool StartWithWindows = false);

public sealed record KeyboardShortcuts(
    string CommandPalette = "Ctrl+Shift+P",
    string CycleTabForward = "Ctrl+Tab",
    string CycleTabBackward = "Ctrl+Shift+Tab",
    string RenameTab = "F2",
    string FindInTab = "Ctrl+F",
    string GlobalFind = "Ctrl+Shift+F",
    string Settings = "Ctrl+OemComma",
    string TogglePlanPanel = "Alt+P",
    string ToggleTaskPanel = "Ctrl+Shift+T",
    string ToggleFileExplorerPanel = "Alt+E");
