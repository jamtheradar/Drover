# Claude Code cheat sheet

Quick reference for everyday use. Press `Esc` to close · type in the search box to filter sections.

Tracks Claude Code v2.1.159 (upstream source: cc.storyfox.cz, by @phasE89).

---

## Keyboard shortcuts (in Claude Code)

General:

- `Ctrl+C` — cancel input / interrupt generation
- `Ctrl+D` — exit the session
- `Ctrl+L` — clear prompt input and force a full-screen redraw
- `Ctrl+O` — toggle transcript viewer (cycles focus view in fullscreen)
- `Ctrl+U` — clear the entire input buffer
- `Ctrl+Y` — restore the cleared input buffer
- `Ctrl+G` — open the prompt in your editor
- `Ctrl+R` — reverse-search prompt history
- `Ctrl+T` — toggle the task list
- `Ctrl+B` — background a running task (keep working while it runs)
- `Ctrl+X Ctrl+K` — kill all background agents
- `Esc Esc` — rewind or summarize (restore code, conversation, or both)
- `Shift+Tab` — cycle permission modes: normal → auto-accept → plan

Mac Option keys:

- `Option+P` — switch model
- `Option+T` — toggle extended thinking
- `Option+O` — toggle fast mode

Input:

- `\` then `Enter` — insert a newline
- `v` — Vim visual mode · `V` — Vim visual-line mode
- `#` at start of message — append a memory to `CLAUDE.md`
- `!` at start of message — run a shell command in this session
- `@path/to/file` — attach a file or directory to the message
- Drag-and-drop an image into the terminal — attach a screenshot

## Drover shortcuts

- `Ctrl+Shift+P` — command palette
- `Ctrl+Tab` / `Ctrl+Shift+Tab` — cycle tabs
- `Ctrl+1`..`Ctrl+9` — jump to tab N
- `F2` — rename current tab
- `Ctrl+F` — find in current tab
- `Ctrl+Shift+F` — search across all session logs
- `Alt+P` — toggle plan panel
- `Ctrl+Shift+T` — toggle tasks panel
- `Alt+K` — toggle kanban board (drag local tasks · live TodoWrite mirror · agents catalog)
- `Ctrl+,` — settings
- `Ctrl+Shift+\`` — bring Drover to the foreground (global)
- `F1` — this cheat sheet
- Drag a tab header sideways to reorder; drag it down out of the strip to tear off into a popped-out window.

## Command palette (Ctrl+Shift+P)

- Type any text to filter actions (open project, switch tab, install hook, etc.)
- `> message` — send `message` to a single tab; pick the target from the result list.
- `>> message` — broadcast `message` to **every** open tab in one shot. Useful for `/clear`, `/compact`, or sending the same prompt to a fan-out of worktree tabs.

---

## Slash commands

### Session

- `/clear` — clear the conversation
- `/compact [focus]` — compact context (optionally focused)
- `/branch [name]` — branch the conversation
- `/rewind` — rewind conversation or code to a checkpoint (`/undo` is an alias)
- `/recap` — summarize session context when returning
- `/usage` — token usage, cost, and cache breakdown
- `/context` — visualize context in a grid
- `/diff` — interactive diff viewer
- `/copy [N]` — copy the last (or Nth) response
- `/export` — export the conversation
- `/plan [desc]` — enter plan mode directly
- `/goal [desc]` — set a completion goal with live progress
- `/resume [session]` — resume by ID or name
- `/rename [name]` — rename the current session
- `/focus` — toggle focus view in fullscreen
- `/help` — show help and commands

### Configuration

- `/config [key [value]]` — view or set settings
- `/model [model]` — switch model
- `/fast [on|off]` — toggle fast mode
- `/effort [level]` — set effort level (interactive slider)
- `/theme [name]` — create and switch custom themes
- `/color [color]` — set the prompt-bar color
- `/permissions` — view and update permissions
- `/keybindings` — customize keyboard shortcuts
- `/scroll-speed [speed]` — adjust output scroll speed
- `/terminal-setup` — configure terminal keybindings

### Tools, skills & review

- `/init` — create `CLAUDE.md`
- `/memory` — edit `CLAUDE.md` files and toggle auto memory
- `/mcp` — manage MCP servers
- `/hooks` — manage hooks
- `/skills` — list available skills
- `/reload-skills` — reload skills without restarting
- `/agents` — manage agent configurations
- `/workflows` — view and manage background workflow runs
- `/review [PR]` — review a PR locally
- `/ultrareview [PR#]` — cloud review with parallel multi-agent analysis
- `/security-review` — scan the diff for vulnerabilities
- `/code-review [effort]` — code review (optional `--fix`)
- `/batch` — large parallel changes across 5–30 worktrees
- `/debug [desc]` — troubleshoot from the debug log
- `/loop [interval] [prompt]` — recurring task
- `/ide` — IDE integration status
- `/add-dir <path>` — add a working directory

### Other

- `/btw <question>` — ask a side question without adding to the conversation
- `/voice` — toggle push-to-talk voice dictation
- `/insights` — analyze a sessions report
- `/extra-usage` — extra usage when rate limited
- `/desktop` — continue in the Desktop app
- `/doctor` — diagnose installation / config
- `/feedback` — submit feedback

---

## MCP servers

Add a server (transport = `http` recommended, `stdio` local, `sse` remote):

```
claude mcp add --transport http <name> <url>
claude mcp add --transport stdio <name> <command> [args...]
claude mcp add --transport sse  <name> <url>
claude mcp add --scope user <name> ...
```

Manage:

```
claude mcp list
claude mcp remove <name>
```

Scopes: **local** `~/.claude.json` (individual) · **project** `.mcp.json` (shared/VCS) · **user** `~/.claude.json` (global).

Server keys: `alwaysLoad: true` keeps it connected across sessions · `maxResultSizeChars` raises the per-tool text threshold (up to 500K). Tool calls inside Claude: `mcp__<server>__<tool>`. Use `/mcp` to inspect connection state.

---

## Memory & CLAUDE.md

Files Claude auto-loads at session start:

- `./CLAUDE.md` or `./.claude/CLAUDE.md` — project level (team-shared)
- `./CLAUDE.local.md` — local personal notes (gitignored)
- `~/.claude/CLAUDE.md` — personal, applies everywhere
- `/etc/claude-code/CLAUDE.md` — managed org policy (Linux/WSL)

Rules & imports:

- `.claude/rules/*.md` · `~/.claude/rules/*.md` — project / user rules
- `paths:` frontmatter — path-specific rules
- `@path/to/file` — import another file inline

Auto memory:

- `~/.claude/projects/<proj>/memory/` — auto-load location
- `MEMORY.md` — auto-loads at startup (first 25KB / 200 lines); topic files load on demand
- Quick-add: start a prompt with `#` to append a memory entry

---

## Skills & agents

Built-in skills surface as slash commands (`/code-review`, `/batch`, `/debug`, `/loop`, `/claude-api`, …). Custom skills live in `.claude/skills/<name>/` (project) or `~/.claude/skills/<name>/` (personal).

Skill frontmatter:

```
description        # auto-invocation trigger
allowed-tools      # skip permission prompts
disallowed-tools   # block specific tools
model / effort     # override for this skill
paths: [globs]     # path-specific
context: fork      # run in a subagent
```

Tokens: `$ARGUMENTS` (user input), `${CLAUDE_SKILL_DIR}`, `${CLAUDE_EFFORT}`, `` !`cmd` `` (dynamic context injection). Ship executables in `plugin bin/` for the Bash tool.

Built-in agents: **Explore** (fast read-only, Haiku) · **Plan** (research for plan mode) · **General** (full tools) · **Bash** (terminal, separate context).

Agent frontmatter: `permissionMode` (default/acceptEdits/plan/dontAsk/bypassPermissions) · `isolation: worktree` · `memory` (user|project|local) · `background: true` · `maxTurns` · `initialPrompt`. Resume agents via `SendMessage`; mention named subagents with `@agent-name`.

---

## CLI commands & flags

```
claude                                       # interactive
claude "fix the failing test"                # one-shot prompt
claude -p "..."                              # headless / print mode
claude -c                                    # continue last conversation
claude -r "<id|name>"                        # resume specific session
claude update                                # update the CLI
claude auth login [--sso|--console]          # sign in
claude project purge [path]                  # delete all project state
claude plugin init <name>                    # scaffold a plugin
claude ultrareview [target]                  # non-interactive cloud review
```

Key flags:

- `--model` — set model
- `-n` / `--name` — session name
- `--resume` / `-r`, `--continue` / `-c` — resume / continue
- `--add-dir` — extra working directory
- `--agent` — use a named agent
- `--allowedTools` / `--disallowedTools` — pre-approve / remove tools
- `--allow-danger` — add bypass-permissions to the mode cycle
- `--dangerously-skip-permissions` — skip all prompts (trusted code only)
- `--permission-mode` — default/acceptEdits/plan/auto/dontAsk/bypassPermissions
- `--effort` — low/medium/high/xhigh/max
- `--output-format` — text/json/stream-json
- `--max-budget-usd` — cost cap
- `--remote` — web session on claude.ai
- `--from-pr` — load PR context
- `--settings <file>` — load settings JSON
- `--debug [filter]` — debug logging

---

## Configuration files

Settings (later overrides earlier):

- `~/.claude/settings.json` — user
- `.claude/settings.json` — project (shared)
- `.claude/settings.local.json` — local only
- `~/.claude.json` — OAuth, MCP, and state
- `.mcp.json` — project MCP servers
- `managed-settings.d/` — drop-in policy fragments

Notable keys: `model`, `permissions.allow`/`.deny`, `hooks` (with `if` conditionals, `defer`, `continueOnBlock`, `type: "mcp_tool"`), `env`, `theme`, `statusLine` (`refreshInterval`), `modelOverrides`, `autoMode.hard_deny`, `showThinkingSummaries`, `disableSkillShellExec`.

---

## Environment variables

- `ANTHROPIC_API_KEY` — auth token
- `ANTHROPIC_MODEL` — default model
- `ANTHROPIC_BASE_URL` — proxy / gateway override
- `ANTHROPIC_BETAS` — additional beta headers
- `ANTHROPIC_CUSTOM_MODEL_OPTION` — custom model entry
- `MAX_THINKING_TOKENS` — set `0` to disable thinking
- `ENABLE_PROMPT_CACHING_1H` — opt into 1-hour cache TTL
- `API_TIMEOUT_MS` — API timeout (default 600000)
- `MCP_TIMEOUT` — MCP startup timeout
- `CLAUDECODE` — `1` inside a Claude shell
- `CLAUDE_CODE_SESSION_ID` — unique session ID for hooks
- `CLAUDE_CODE_DISABLE_CRON` — disable scheduled tasks
- `CLAUDE_CODE_DISABLE_AUTO_MEMORY` — disable auto memory
- `CLAUDE_CODE_DISABLE_1M_CONTEXT` — disable 1M context
- `CLAUDE_CODE_ENABLE_AUTO_MODE` — enable auto mode
- `DISABLE_UPDATES` — block all update paths
- `DISABLE_TELEMETRY` — opt out of telemetry

---

## Workflows & tips

- **Plan mode**: `Shift+Tab` cycles normal → auto-accept → plan; or `--permission-mode plan`. Plan files are named after the prompt (e.g. `fix-auth-race-snug-otter.md`).
- **Thinking & effort**: `Option+T` toggles thinking; say *ultrathink* for max effort on a turn; `Ctrl+O` to view thinking; `/effort` for the slider.
- **Git worktrees**: `--worktree name` or `isolation: worktree` for an isolated branch per feature; `/batch` auto-creates them; `sparsePaths` checks out only needed dirs.
- **Voice**: `/voice` enables push-to-talk — hold `Space` to record, release to send (20 languages).
- **Context**: `/context` for usage + tips; `/compact [focus]` to compress; Opus supports 1M context.
- **Session power moves**: `claude -c` to continue, `claude -r "name"` to resume by name, `/btw` for a side question without context cost, `cat file | claude -p` to pipe input, `--max-budget-usd 5` to cap cost.
- **Scheduling & remote**: `/loop 5m msg` for recurring tasks; `--remote` for a web session; `!` runs a shell command as a background session.

---

## Hooks

Lifecycle events that trigger shell commands. Configure in `settings.json`:

```
"hooks": {
  "PreToolUse":   [...],
  "PostToolUse":  [...],
  "UserPromptSubmit": [...],
  "Stop":         [...],
  "SubagentStop": [...],
  "Notification": [...],
  "PreCompact":   [...],
  "SessionStart": [...]
}
```

Drover ships its own hook gateway on `127.0.0.1:17923` — install per-project from the project sidebar's right-click menu under **Drover → Install Drover hook in project**.

---

## Troubleshooting

- **Hook not firing** → check `~/.claude/settings.json` syntax and that the matcher pattern matches.
- **MCP server stuck** → `/mcp` to inspect; remove and re-add from a shell.
- **Context full** → `/compact` to summarize, or `/clear` to reset.
- **Wrong model picked up** → `/usage` / `/config` show the resolved model; override with `/model` or `--model`.
- **Permission prompts every turn** → add the tool to `permissions.allow` in `settings.json`.
- **`claude` not on PATH** → `claude doctor` reports installer health.
