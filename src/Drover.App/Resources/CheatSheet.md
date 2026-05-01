# Claude Code cheat sheet

Quick reference for everyday use. Press `Esc` to close · type in the search box to filter sections.

---

## Keyboard shortcuts (in Claude Code)

- `Shift+Tab` — cycle modes: normal → auto-accept → plan
- `Ctrl+C` — interrupt the current turn
- `Ctrl+D` — exit the session (or close stdin)
- `Ctrl+L` — clear the screen (history kept)
- `Ctrl+R` — toggle verbose tool output (expanded vs collapsed)
- `Ctrl+T` — show the to-do list
- `Up` / `Down` — navigate prompt history
- `Esc` — cancel an in-progress prompt before sending
- `Esc Esc` — fork the conversation from a previous turn
- `\` then `Enter` — insert a newline in the prompt
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

- `/clear` — wipe context, start fresh in the same window
- `/compact [instructions]` — summarize and continue (saves context)
- `/resume` — pick a prior conversation to continue
- `/exit` (or `/quit`) — leave the session
- `/help` — list available commands

### Configuration

- `/config` — open the settings UI
- `/model [name]` — switch model (`sonnet`, `opus`, `haiku`, or full ID)
- `/theme` — change the color theme
- `/login` / `/logout` — manage your Anthropic auth
- `/status` — show account, model, MCP, and rate-limit info
- `/permissions` — review and edit allow/deny lists

### Plan & review

- `/plan` — enter plan mode (read-only research, no edits)
- `/review` — review the current branch / PR
- `/security-review` — security audit of pending changes
- `/init` — initialize a new `CLAUDE.md` from the codebase

### Loops & schedules

- `/loop <interval> <prompt>` — run a prompt every N minutes (`5m`, `1h`)
- `/loop <prompt>` — let Claude self-pace iterations
- `/schedule` — create or list scheduled background agents

### Memory & files

- `/memory` — open the memory editor
- `/add-dir <path>` — add another directory to the workspace
- `#` (start of prompt) — quick-add a fact to project memory

### Skills & agents

- `/skills` — list installed skills
- `/agents` — manage subagents

### Other

- `/mcp` — manage MCP servers (list, add, remove, restart)
- `/cost` — show token spend for this session
- `/doctor` — diagnose installation / config issues
- `/bug` — file a bug report from the CLI

---

## MCP servers

Add a server (scope = `local`, `project`, or `user`):

```
claude mcp add <name> <command> [args...]
claude mcp add --transport http <name> <url>
claude mcp add --transport sse  <name> <url>
claude mcp add --scope user <name> ...
```

Manage:

```
claude mcp list
claude mcp remove <name>
claude mcp restart <name>
```

Tool calls inside Claude: `mcp__<server>__<tool>`. Use `/mcp` from inside a session to inspect connection state.

---

## Memory & CLAUDE.md

Files Claude auto-loads at session start, in order:

- `~/.claude/CLAUDE.md` — personal, applies everywhere
- `<repo>/CLAUDE.md` — project, checked in
- `<repo>/CLAUDE.local.md` — project, gitignored
- `<repo>/.claude/CLAUDE.md` — alternate project location

`@import` syntax inside any of those pulls another file inline:

```
@~/.claude/personal-prefs.md
@./docs/architecture.md
```

Quick-add: start a prompt with `#` to append a memory entry (Claude picks the file).

---

## Workflows & tips

- **Plan mode** (`Shift+Tab` twice, or `/plan`): no file edits, no shell side-effects — use to explore, then exit plan mode to execute.
- **Auto-accept** (`Shift+Tab` once): skips per-tool confirmations. Pair with `--dangerously-skip-permissions` only on trusted code.
- **Thinking budgets**: include words like *think*, *think hard*, *think harder*, *ultrathink* in your prompt to scale reasoning effort. Drover surfaces the active budget in the tab header.
- **Worktrees**: run independent agents in parallel with `git worktree add ../feat-x feat-x` — each gets its own session and file lock.
- **Voice input**: hold `Fn` (Mac) or use Windows dictation (`Win+H`) to dictate prompts.
- **Screenshots**: drag into the terminal, or paste with `Ctrl+V` if your terminal supports image paste.
- **Fork at a turn**: `Esc Esc` to rewind and try a different direction without losing the original branch.
- **Stop runaway tools**: `Ctrl+C` once cancels the current call; twice exits the turn.
- **Compact early**: `/compact` before context gets tight — preserves the thread, drops raw tool spam.

---

## Config & environment

Settings files (later overrides earlier):

- `~/.claude/settings.json` — user-global
- `<repo>/.claude/settings.json` — project, checked in
- `<repo>/.claude/settings.local.json` — project, gitignored

Common keys: `model`, `permissions.allow` / `.deny`, `hooks`, `env`, `apiKeyHelper`, `theme`, `statusLine`.

Environment variables:

- `ANTHROPIC_API_KEY` — auth token
- `ANTHROPIC_MODEL` — default model
- `CLAUDE_CODE_USE_BEDROCK=1` — route via AWS Bedrock
- `CLAUDE_CODE_USE_VERTEX=1` — route via GCP Vertex
- `DISABLE_TELEMETRY=1` — opt out of telemetry
- `DEBUG=1` — verbose CLI logs

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

Drover ships its own hook gateway on `127.0.0.1:17923` — install per-project from a tab's context menu (**Install Drover hook in project**).

---

## CLI flags

```
claude                                       # interactive
claude "fix the failing test"                # one-shot prompt
claude -c                                    # continue last conversation
claude -r <session-id>                       # resume specific session
claude -p "..."                              # print mode (non-interactive)
claude --model sonnet                        # pick model
claude --add-dir ../shared-lib               # extra workspace dir
claude --allowedTools "Bash,Edit,Read"       # restrict tools
claude --dangerously-skip-permissions        # YOLO — trusted code only
claude --output-format json                  # machine-readable output
claude --max-turns 5                         # cap automated loop
```

Subcommands: `claude mcp`, `claude config`, `claude doctor`, `claude migrate-installer`.

---

## Skills

Skills live in `~/.claude/skills/<name>/SKILL.md` (user) or `<repo>/.claude/skills/<name>/SKILL.md` (project). Frontmatter:

```
---
name: my-skill
description: When to use this skill
---
```

Invoke with `/<skill-name>` or let Claude pick it based on the description match.

---

## Subagents

Defined in `~/.claude/agents/<name>.md` or `<repo>/.claude/agents/<name>.md`:

```
---
name: my-agent
description: What this agent does
tools: Read, Grep, Glob
model: sonnet
---
System prompt for the agent...
```

Built-in: `general-purpose`, `Explore`, `Plan`, plus task-specific reviewers and architects.

---

## Troubleshooting

- **Hook not firing** → check `~/.claude/settings.json` syntax and that the matcher pattern matches.
- **MCP server stuck** → `/mcp` to inspect, `claude mcp restart <name>` from a shell.
- **Context full** → `/compact` to summarize, or `/clear` to reset.
- **Wrong model picked up** → `/status` shows the resolved model; override with `/model` or `--model`.
- **Permission prompts every turn** → add the tool to `permissions.allow` in `settings.json`.
- **`claude` not on PATH** → `claude doctor` reports installer health.
