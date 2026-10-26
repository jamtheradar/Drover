---
id: NNNN
title: ""
status: pending          # pending | in-progress | blocked | done
created: YYYY-MM-DD
depends_on: []           # list of spec ids, e.g. [0003, 0007]
estimate: ""             # rough size: xs | s | m | l | xl
agent: ""                # subagent to implement this, from .claude/agents (project) or ~/.claude/agents; "" = main session
verify_with: ""          # reviewer subagent whose concurrence gates status: done; "" = self-assessed
last_run_at: ""          # ISO-8601 local time, set by the loop runner
block_severity: ""       # "" | trivial | major (set only when blocked)
---

# {{title}}

## Context

One paragraph. Why this spec exists. What's broken, missing, or being added.
Link to the tasks.md line, issue, or conversation that prompted it.

## Affected files

Concrete file paths. Use globs only when truly unbounded.

- `path/to/file.cs` — what changes here, briefly
- `path/to/other.xaml` — what changes here, briefly

## Approach

Bullet points or short paragraphs. The implementation strategy at a level
that lets a reviewer say "yes, do that" without reading the code.

## Acceptance criteria

A checklist of observable, testable outcomes. The loop runner uses these
to decide if the spec is done. Each item must be something you could
demonstrate in a screen share.

- [ ] ...
- [ ] ...
- [ ] ...

## Out of scope

What this spec does *not* cover. Prevents drift mid-implementation.

- ...

## Test plan

How "green" is verified. The exact command(s) the loop runner should
execute, and what passing looks like.

```
# command(s)
```

## Notes

Optional. Anything else worth capturing — design alternatives considered
and rejected, links, screenshots, etc.
