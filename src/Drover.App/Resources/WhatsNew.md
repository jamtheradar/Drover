# What's new in Drover

## 0.4.0

- **Per-project git status** in the sidebar and on tab headers — branch, dirty marker, ahead/behind counts. Polled in the background; no-op if `git` isn't on PATH.
- **Per-tab live activity** strip ("Reading file…", "Running bash…", "Searching code…") sourced from Claude Code tool-call hooks. Decays after a few seconds of silence.
- **Permission-prompt surface.** When Claude Code is waiting for an allow/deny decision, the tab header now shows a dismissible amber strip with the prompt text, plus a toast and taskbar flash if the tab isn't already foreground.
- **Tab drag-to-reorder** in the strip and **drag-out tear-off** into a popped-out window — drag a tab header off the strip to detach it.
- **Idle-tab count in the window title** when Drover is backgrounded ("Drover · 2 waiting"), so a glance at the taskbar tells you which window has Claude Code waiting on you.
- **Bulk project import.** New "+ Import folder of repos…" link in the sidebar scans a parent folder (optionally up to 3 levels deep) for git repos and lets you check-box your way to a populated catalog.
- **Command palette** now hints at the `>` (send to one tab) and `>>` (broadcast to all tabs) prefixes via inline placeholder text.
- **General settings** tab adds: custom `claude` executable path, default `--model` for new tabs, global `KEY=VALUE` env vars layered onto every Claude/pwsh launch, session-log retention knobs, "Start Drover with Windows" toggle, and a markdown theme picker.
- **Direct-launch Claude tabs** (`ClaudeDirect` project kind) — bypass the pwsh wrapper and run `claude.exe` straight through CreateProcess. Working directory and env (`DROVER_SESSION_ID`, `DROVER_HOOKS_URL`, global + per-project vars) ride on the native API. Faster startup, no shell quirks. A per-project `LaunchCommand` field also exposes a raw command-line override for shell experiments (cmd.exe, git-bash, nu, …).
- **App-wide crash log** at `%APPDATA%\Drover\logs\app.log` — dispatcher, AppDomain, and unobserved-task exceptions all land here instead of disappearing into the void. UI-thread exceptions show a one-shot dialog and the shell stays up. Velopack update diagnostics flow into the same file. Logs older than 30 days are pruned on launch.
- **Configurable session log retention.** The `%APPDATA%\Drover\logs` folder cap (default 50 files / 500 MB) is now exposed as two settings — set either to 0 to disable that side of the cap.

## 0.3.0

- Status bar now shows the running app version next to the activity text.
- Drag and drop now works for images.
- Fixed shortcut keys working in claude code. 

## 0.2.0

- **About dialog** with version info, accessible from the sidebar.
- **What's New** dialog now appears the first time you launch a new version.
- **File Explorer panel** (Alt+E) for browsing the project tree alongside terminals.
- Vendored `EasyWindowsTerminalControl` so the ConPTY host can no longer disappear from a stale dependency.
- Clipboard paste fixes inside Claude Code sessions.

## 0.1.0

- Initial public build: tabbed Claude Code sessions, plan/task panels, dashboard, tokenomics, and markdown memory editor.
- Hooks gateway for live statusLine, attention, and cost telemetry from Claude Code.
- Global Ctrl+Shift+\` hotkey, command palette (Ctrl+Shift+P), and per-tab attention monitoring.
