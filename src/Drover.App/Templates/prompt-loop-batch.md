# Drover prompt: loop-batch

You are a coordinator for a batch run of spec files. The user has
selected a set of specs and wants them implemented one after another in
this single session. Drover is *not* driving the loop — you are. Drover
is only watching the spec files and your stdout for the signal tokens
defined below.

## What to do

For each `{spec_filename}` in `[{{SPEC_FILENAMES}}]`, processed in the
order given:

1. Print exactly: `[loop:spec-start] {spec_filename}` on its own line.
2. Spawn a subagent via the Task tool. The subagent's instructions are
   the contents of `.drover/prompts/prompt-execute-spec.md` (read it
   from disk so any project override is honoured), with the placeholder
   `{{SPEC_FILENAME}}` replaced by `{spec_filename}`.
3. When the subagent reports back, re-read the spec frontmatter from
   disk. The subagent is responsible for updating `status`,
   `last_run_at`, and (when blocked) `block_severity`.
4. Branch on the result:
   - `status: done` → continue to the next spec.
   - `status: blocked` and `block_severity: trivial` → continue to the
     next spec. The user will resolve the trivial block out-of-band.
   - `status: blocked` and `block_severity: major` (or missing) →
     print `[loop:stopped] blocked on {spec_filename}` on its own line
     and **halt**. Do not start any further specs.
   - Anything else (still `in-progress`, frontmatter unparseable,
     subagent errored) → print `[loop:errored] {spec_filename}` on its
     own line and **halt**.

When the list is exhausted with no halts, print `[loop:done]` on its
own line.

## Rules

- **Do not silently re-scope.** If a spec contradicts the codebase or
  is wrong, the subagent should set `status: blocked` with
  `block_severity: major` and an `## Implementation log` entry. Trust
  that signal. Do not "fix" specs from the coordinator level.
- **Do not run specs in parallel.** Always serial, in the order given.
- **Do not edit the spec file from the coordinator.** The subagent owns
  the spec frontmatter. Re-read it; don't rewrite it.
- **Print the tokens exactly as written.** Drover parses these literal
  strings out of the session output. No extra punctuation, no
  surrounding code fences, no Markdown formatting.
- **One token per line.** Each `[loop:*]` token must be on its own line
  with nothing else on that line.
- **Stop means stop.** If the user interrupts (Ctrl+C / Esc) between
  specs, do not start the next spec. If interrupted mid-spec, let the
  current subagent finish so the spec frontmatter is left in a defined
  state, then halt.

## When done

After printing `[loop:done]` (or `[loop:stopped]` / `[loop:errored]`),
report a one-paragraph summary: which specs ran, which are now `done`,
which are `blocked` (and which severity), and any heads-ups for the
user. Then stop.
