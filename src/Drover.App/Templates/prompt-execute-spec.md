# Drover prompt: execute-spec

You are implementing a single spec from `specs/`. The spec is the source
of truth for what changes; your job is to make those changes, verify
them, and update the spec frontmatter to reflect the outcome.

## What to do

1. Read `specs/{{SPEC_FILENAME}}` end to end before touching any code.
   (If the placeholder is still present, ask the user which spec to
   execute and stop.)
2. Read `CLAUDE.md` and any skills referenced in the spec's *Approach*
   section.
3. Set the spec's frontmatter `status: in-progress` before starting work.
   Do not batch this with other edits — flip status first, then implement.
4. Implement the *Approach* and meet the *Acceptance criteria*. Honour
   every box in the *Constraints* section. Stay inside *Out of scope* —
   if a change you'd otherwise make is listed there, don't make it.
5. After implementation, execute the *Test plan* verbatim. Run every
   command listed. For manual steps you cannot perform (device runs,
   external service calls, anything requiring physical hardware or
   credentials you don't have), report them honestly as not-verified
   rather than skipping or claiming success.
6. Update the spec frontmatter:
   - All acceptance criteria pass (including manual steps you could
     perform) → `status: done`
   - One or more criteria block → `status: blocked` and add a short
     `## Implementation log` section at the bottom of the spec
     describing what blocked and why
   - Always set `last_run_at` to the current local time in ISO-8601
     format (e.g., `2026-05-04T17:32:11+10:00`), regardless of outcome.
     This is the cheapest "did this spec run recently" signal — set it
     even if the run produced nothing usable.
   - When setting `status: blocked`, also set `block_severity` to
     either `trivial` or `major`. The loop runner uses this to decide
     whether to continue the batch:
     - **trivial**: the spec is unblocked by a single non-judgement
       action the user can do without reasoning (run the manual test,
       verify on a device, plug in a credential, restart a service).
       The implementation is otherwise complete and correct.
     - **major**: the spec is wrong, contradicts the codebase, requires
       a decision the user has to make, or the implementation is
       incomplete in a way that needs more work. Anything that
       requires the spec itself to be edited or re-thought.
     If you genuinely cannot tell, choose `major` — stopping is the
     safer default.
7. Report a short summary in the chat: what changed, which acceptance
   criteria pass, which block, and any heads-ups for the user.

## Rules

- **Spec is the source of truth.** If the spec is silent on something,
  follow `CLAUDE.md`. If both are silent, ask — do not improvise.
- **No silent re-scoping.** If you discover the spec is wrong,
  incomplete, or contradicts the codebase mid-implementation, stop and
  tell the user. Do not "fix" the spec by inferring intent.
- **Pin specific package versions.** When adding or upgrading a NuGet,
  npm, or other package, pin the latest stable version that matches the
  project's existing version family. Never use "compatible with X" or
  floating version ranges. State the exact version chosen in your
  summary. If the right version isn't obvious, ask before adding.
- **Watch for shared resources.** If your change modifies a shared
  registration (named `HttpClient`, DI singleton, static config, build
  property, etc.), enumerate the other consumers and confirm the change
  is safe for all of them. If preserving existing behaviour for an
  out-of-scope consumer requires a config tweak, make the minimum tweak
  and flag it explicitly in your summary. Do not silently change
  behaviour for code listed under *Out of scope*.
- **Honest verification.** Build success is not the same as acceptance.
  An acceptance criterion that requires a runtime check is not satisfied
  by `dotnet build` passing. Mark such criteria as not-verified and
  block accordingly.
- **Constraints are checkboxes, not suggestions.** Every box in the
  spec's Constraints section must be ticked, marked N/A with a reason,
  or explicitly called out as violated (with justification). Don't skip
  the section.

## Implementation log format

When `status: blocked`, append (don't replace) a section like:

```markdown
## Implementation log

**Date:** YYYY-MM-DD
**Outcome:** blocked

What was completed:
- ...

What blocks:
- ...

Heads-ups for follow-up:
- ...
```

Keep it short. Three to six bullets total is fine. The user will read
this when they pick up the spec to finish manual verification.

## When done

Stop. Do not start the next spec. Do not edit `tasks.md`. The user
triggers the next action.
