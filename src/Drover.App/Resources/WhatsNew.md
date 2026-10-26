# What's new in Drover

## 0.8.0

- **Workflow panel.** A new front door to the plan → tasks → spec → loop pipeline. It explains each stage, shows what exists for the current project, and behind each stage's ⓘ lists every path it searched with a tick or cross — so an empty stage tells you exactly which file is in the wrong place. **Generate tasks from plan** derives `- [ ]` lines from a plan into `TASKS.md`, appending only.
- **One spec from several tasks.** Tick related lines in the Tasks panel and choose **Create spec from selected tasks…** to promote them as a single coherent spec instead of one spec per line. Task and section context menus also gain mark complete / incomplete and *Send section as prompt*.
- **Open folders in Drover.** Optional Explorer **Open in Drover** entry on folders, plus a `drover` command on PATH (`drover` opens the current folder, `drover <path>` opens that path). A launch with a folder hands it to the running Drover as a new tab rather than starting a second window. No admin needed; toggle in Settings.
- **Faster, sturdier hooks.** Hook events now go through a tiny `drover-hook.exe` forwarder instead of PowerShell scripts, which cuts per-tool-call overhead noticeably. It's silent and always exits cleanly, so a missing or busy Drover never blocks a Claude Code turn.
- **Claude defaults for new projects.** Drover can seed `.claude/settings.local.json` with a sensible `env` block and permission allow/deny lists. Merge-only — your existing values always win — and it removes deny rules Claude Code now rejects at startup.
- **Shortcuts no longer clash with Claude Code.** Drover's defaults move to Ctrl+Shift+letter, F-keys, Ctrl+Tab and Ctrl+1..9; bare Ctrl+letter and Alt+letter are left for the session (e.g. Alt+P is Claude's model picker again). Bindings still on an old default are migrated automatically; customised ones are left alone, and Settings warns if you bind a Claude Code key. The F1 cheat sheet now shows your actual bindings.
- **Copy and paste.** `Ctrl+C` with a selection (or `Ctrl+Shift+C`) copies; `Ctrl+V` / `Ctrl+Shift+V` pastes text, screenshots, and files copied in Explorer.
- **Mouse clicks off in fullscreen Claude.** Claude tabs set `CLAUDE_CODE_DISABLE_MOUSE_CLICKS=1` by default so a click can't land on a stale option list and pick the wrong answer. Wheel scrolling still works.
- **Leaner layout options.** Collapse the project sidebar with a shortcut, hide the top bar, and switch off the spec pipeline UI (Specs, Workflow, Kanban) entirely if you don't use it — it's off by default on fresh installs.
- **Hooks prompt asks once.** The "enable Drover hooks?" offer no longer repeats for a project you've already answered.
- **Under the hood:** Windows SDK target bumped to 26100; ConPTY 1.25, Velopack 1.2.161, Microsoft.Extensions.Hosting 10.0.13, SharpVectors 1.8.6. Settings JSON is written without HTML-escaping so hook commands stay readable.

## 0.7.0

- **Specs can name their agent.** A spec's new `agent:` frontmatter field names the subagent that should implement it. Drover threads the name into the execute prompt and the session delegates via the Task tool — while keeping ownership of the spec's frontmatter, so a subagent can't race the loop runner. Leave it empty (the common case) and the spec is implemented in-session as before. Promoting a task now proposes a fitting agent instead of leaving the field blank.
- **Reviewer gate before `done`.** A spec's new `verify_with:` field names an independent reviewer that has to pass every acceptance criterion before `status: done` can be written. Until now the implementing agent was the sole judge of its own work — one unearned `done` and an overnight batch marches on. A reviewer-driven failure comes back as an ordinary `blocked`, so the existing stop policy handles it; a missing reviewer just degrades to self-assessment and says so.
- **Run one spec, or run it with an agent.** Right-click a row in the Specs panel for **Run this spec** — one spec, no need to untick everything else — or **Run with agent ▸** to run it against a chosen agent for this run only, nothing written back to the file. Both go through the loop runner, so you keep status watching, the progress strip, the timeout, and Stop.
- **The Agents rail is a dispatch board.** Each agent card in the kanban Agents view now shows a done/assigned spec count, chips for the spec ids assigned to it, and a marker for the spec it's working on right now. All read from `specs/*.md`, so it's accurate whether Drover launched the run or you typed the prompt yourself.
- **Initialize specs seeds starter agents.** *Project ▸ Drover ▸ Initialize specs* now also drops `spec-implementer.md` and `spec-reviewer.md` into `.claude/agents/`, so the new fields and the **Run with agent** menu have something to point at on a fresh project. They're generic starting points — edit them for your project. Existing agents with those names are left untouched.
- **Upgrading:** specs and templates from earlier versions work unchanged; both new fields default to empty, which behaves exactly as 0.6.0 did. Drover never rewrites `.drover/`, so to get the new fields in your spec template, add them by hand or delete `.drover/spec-template.md` and re-run Initialize specs.

## 0.6.0

- **Session history browser.** New **Session history** window (sidebar footer, or from the command palette) lists every past session's Markdown transcript across all your projects, newest first, rendered as a formatted document. Type to search the whole corpus — the session list filters to matching sessions with match counts, hits are highlighted in the open transcript with tick marks on the scrollbar, and Enter/F3/Shift+F3 walk between them. Filter by project; double-click a session to open the underlying file.
- **Export & Find now work at the default logging level.** Export history, the in-tab Find window, and "Open session log" used to need the raw VT trace that only exists when session logging is set to Full — on default settings they silently did nothing. They now prefer the clean Markdown transcript, exports keep real Markdown formatting instead of a stripped terminal dump inside a code fence, and when no log is on disk you get a clear status message (and an `app.log` entry) instead of silence.
- **Live agents in the Agents panel.** The kanban Agents view now pulses the card of each subagent actually running in the active tab, driven by Task-tool hook events. Built-in agents with no local definition (general-purpose, Explore, plugin agents) get a synthetic card while they run; everything clears at the end of the turn.
- **Claude 5 cost tracking.** Token pricing updated for the Claude 5 family — Fable 5 / Mythos 5, Opus 4.8 (including Opus's price drop to $5/$25 per MTok), and Sonnet 5. Model ids Drover doesn't recognise yet now fall back to family pricing, so the next new model shows approximately-right cost figures instead of zeroing the dashboard.
- **Cheat sheet refresh.** The F1 cheat sheet now tracks Claude Code v2.1.159 — background tasks (`Ctrl+B`), rewind (`Esc Esc`), `/usage`, `/context`, `/goal`, fast mode, Mac Option keys, and more.
- **Gemini support** — belated release note: Google's Gemini CLI shipped quietly as a third project kind (alongside Claude and Codex) in 0.5.0. Pick it in the New Project dialog; set the executable path and API key in Settings.
- **Under the hood:** Velopack updater bumped to 1.2.0.

## 0.5.0

- **Vault** — a persistent, agent-readable markdown doc store that survives across sessions, tabs, and reboots. Two namespaces: a per-project vault (`<project>/.drover/vault/`, `.gitignore`d by default) and a global vault (`%APPDATA%\Drover\vault\`) for notes that span projects. Plain markdown in nested folders — no DB, no sidecars; you can `cat` or grep it without Drover running. Browse and edit notes from the new **Vault panel**.
- **Vault MCP server.** Claude can read and write the vault directly through an MCP server Drover installs per-project — `vault_read`, `vault_write`, `vault_append`, `vault_list`, and `vault_search`. Sessions can stash and recall context on their own without you copy-pasting between tabs.
- **Skills catalog.** A new **Skills** window pulls agent skills from configurable Git sources and installs them with a click. Manage your sources in the Skills Sources dialog; reuses the existing `.drover/` precedent, no new background services.
- **Codex support.** OpenAI's Codex CLI is now a first-class project kind alongside Claude and pwsh. Codex tabs get the same treatment Claude tabs do — real-time permission prompts, per-tool activity, working/idle taskbar flash and toasts, and session logs — driven by Codex's own hook system rather than terminal scraping. Pick your agent at New-Project time and mix Claude and Codex tabs in one window. Where Codex surfaces less telemetry than Claude, Drover degrades honestly rather than inventing data.

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
