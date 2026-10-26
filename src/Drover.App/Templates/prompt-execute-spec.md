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
4. Check the spec's agent assignment. Drover substitutes it here:

   **Assigned agent:** `{{AGENT}}`

   - If that value is empty, or is still the literal `{{AGENT}}`
     placeholder (Drover didn't supply it), there is no assignment.
     Implement the spec yourself in this session — that's the default
     and the common case.
   - Otherwise, delegate the implementation to that subagent via the
     Task tool: `Use the @{{AGENT}} subagent to: implement
     specs/{{SPEC_FILENAME}}`. Pass the spec *path* so the subagent
     reads the file itself; don't paraphrase or summarise the spec for
     it.
   - **You keep the frontmatter.** The subagent implements and reports
     back; you write `status`, `last_run_at` and `block_severity`. Tell
     the subagent explicitly not to touch the spec's frontmatter —
     Drover watches for `status` leaving `in-progress`, and a subagent
     flipping it early races the loop runner.
   - If the named agent doesn't exist under `<project>/.claude/agents/`
     or `~/.claude/agents/`, don't guess at a near-match and don't
     invent one. Implement in this session as if unassigned, and say in
     your chat summary that the assigned agent was missing.

5. Implement the *Approach* and meet the *Acceptance criteria*. Honour
   every box in the *Constraints* section. Stay inside *Out of scope* —
   if a change you'd otherwise make is listed there, don't make it.
6. After implementation, execute the *Test plan* verbatim. Run every
   command listed. For manual steps you cannot perform (device runs,
   external service calls, anything requiring physical hardware or
   credentials you don't have), report them honestly as not-verified
   rather than skipping or claiming success.
7. Verify with the assigned reviewer. Drover substitutes it here:

   **Verifier:** `{{VERIFY_WITH}}`

   - If that value is empty, or is still the literal `{{VERIFY_WITH}}`
     placeholder, there is no verifier. Assess the criteria yourself as
     before, and say in your chat summary that no independent
     verification happened.
   - If the named agent doesn't exist under `<project>/.claude/agents/`
     or `~/.claude/agents/`, don't substitute a different reviewer and
     don't guess at a near-match. Fall back to self-assessment and say
     so. A missing verifier must never block a run on its own — it
     degrades to today's behaviour, never to a stop.
   - Otherwise spawn that subagent via the Task tool and give it:
     - the spec path, `specs/{{SPEC_FILENAME}}`;
     - the *Acceptance criteria* list, copied verbatim from the spec;
     - the diff, or the list of changed files with their contents;
     - the raw output of the *Test plan* commands.

     Give it the evidence, not your account of it. A verifier reading
     "I implemented X and it works" is worthless — it has to reach its
     own conclusion from the criteria and the diff.
   - Ask for a per-criterion verdict — `pass`, `fail`, or
     `not-verified` — plus a one-line reason for anything that isn't
     `pass`.
   - **The verifier fixes nothing.** Tell it explicitly: no file edits,
     no frontmatter edits, no re-running the implementation. It reports;
     you or a later run act on the report.
   - **Concurrence gates `done`.** You may write `status: done` only if
     the verifier returned `pass` on every criterion. Otherwise
     `status: blocked`, with the severity chosen by the normal rule in
     step 8 applied to *what the verifier found*: a criterion it could
     not check without a device or credential is `trivial`; a criterion
     it says is genuinely unmet is `major`.
   - Do not "just fix it and re-verify" inside this run. That turns a
     bounded run into an unbounded one and hides the disagreement.
     Blocked-with-a-log is the intended outcome; the user re-runs.

8. Update the spec frontmatter:
   - All acceptance criteria pass (including manual steps you could
     perform) *and* the step 7 verifier concurred, if there was one →
     `status: done`
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
9. Report a short summary in the chat: what changed, which acceptance
   criteria pass, which block, which subagents (if any) did the work,
   whether an independent verification happened, and any heads-ups for
   the user.

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
**agents_used:** agent-one, agent-two
**Verified by:** @reviewer-name   # or "not verified — no verifier assigned"

What was completed:
- ...

What blocks:
- ...

Heads-ups for follow-up:
- ...
```

`agents_used:` lists the subagents that actually did work — the one
assigned in `agent:` plus any you chose to spawn yourself. Write `none —
main session` when you implemented it directly. Drover deliberately does
not reconstruct this from hooks (`RunningAgentTypes` is per-tab and
ephemeral); this log line is the durable record.

`Verified by:` records the step 7 verifier and its verdict. Where the
verifier and you disagreed, write both positions — disagreement is
information, not something to resolve silently in favour of either
party. Say `not verified` (with the reason: none assigned, agent
missing, Codex tab) rather than leaving the line off; never imply a
verification that didn't happen.

If a subagent did any of the work, or a verifier ran, write this section
even when the outcome is `done` and you'd otherwise skip the log — a
four-line entry with just **Date**, **Outcome**, **agents_used** and
**Verified by** is enough.

Keep it short. Three to six bullets total is fine. The user will read
this when they pick up the spec to finish manual verification.

## When done

Stop. Do not start the next spec. Do not edit `tasks.md`. The user
triggers the next action.
