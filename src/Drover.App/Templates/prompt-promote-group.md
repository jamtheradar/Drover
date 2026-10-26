# Drover prompt: promote-group-with-interrogation

You are helping the user turn **a group of related `tasks.md` lines into a
single spec** under `specs/`. This is the deliberate antidote to
one-spec-per-trivial-line: the user has hand-picked these lines because they
are one coherent unit of work.

Group label: {{GROUP_LABEL}}

The lines you're working from:

{{TASK_LINES}}

(Drover injects the literal task lines here. If either placeholder is still
present, ask the user which lines in tasks.md they meant and stop.)

## What to do

1. Read `.drover/spec-template.md`. This is the scaffold. Don't deviate
   from its structure.
2. Read `tasks.md` to confirm the lines are still present and grab the
   surrounding context (section headings, sibling tasks).
3. **Sanity-check the grouping.** These lines should share a subject,
   a file set, or a single reviewable outcome. If two or more of them are
   genuinely unrelated, say so, name the odd ones out, and ask whether to
   drop them from this spec or split into more than one — then stop until
   the user answers.
4. List existing specs under `specs/` to determine the next spec id
   (zero-padded, 4 digits, monotonically increasing — e.g., if the
   highest is `0007-foo.md`, the new id is `0008`).
5. Do a quick repo grep to identify the *probable* affected files across
   the whole group. Aim for the obvious files, not an exhaustive list.
6. Pick the implementing agent. Enumerate the agent definition files in
   `~/.claude/agents/` and `<project>/.claude/agents/` (project shadows
   user on a name collision), read each one's `description` frontmatter,
   and choose the best fit. Rules:
   - Write the bare agent `name` (no `@`, no path) into the spec's
     `agent:` frontmatter field.
   - If no discovered agent is a good fit, leave `agent: ""`. That's the
     default main-session path and a perfectly good answer. Do not invent
     a name or guess at an agent that isn't on disk.

   Also consider a **verifier** for the spec's `verify_with:` field — a
   reviewer agent whose concurrence gates `status: done`. Suggest one when
   the acceptance criteria are checkable from the diff; leave
   `verify_with: ""` when they are all manual or runtime. Prefer a
   different agent from the implementer.
7. Ask the user **at most 4** clarifying questions, one at a time, chosen
   from gaps you genuinely have after steps 2-5. Likely areas:
   - "Is there an ordering constraint between these lines?"
   - "What single acceptance criterion would convince you the whole group
     is done?"
   - "Anything in this group explicitly out of scope?"
   - "Do any of these depend on an existing spec?"

   If the group is small and unambiguous, ask zero questions and proceed.
8. Generate `specs/{NNNN}-{slug}.md` from the template, where:
   - `{NNNN}` is the next id
   - `{slug}` is a kebab-case name for the *group* (max 5 words) — name
     the shared outcome, not the first line
   - frontmatter `status` is `pending`
   - frontmatter `created` is today's date in YYYY-MM-DD
   - frontmatter `agent` / `verify_with` are the choices from step 6 (or `""`)
   - Every source line appears in the spec — as its own acceptance
     criterion or as an explicit sub-item of the approach. Nothing the
     user selected may silently vanish.
   - All template sections are filled in. No section may be left as
     placeholder text. If a section truly doesn't apply, write "N/A —
     {reason}".
9. Show the user the proposed spec content, and call out the `agent:` and
   `verify_with:` choices explicitly. Wait for explicit approval before
   writing.
10. On approval, write the file and confirm in one line: "Created
    specs/{NNNN}-{slug}.md from N tasks. Status: pending."

## Constraints

- Do **not** start implementing. This action only creates the spec file.
- Do **not** edit `tasks.md`. The user decides whether to remove the
  promoted lines themselves.
- Do **not** silently split the group into several specs. If it needs
  splitting, stop and propose the split first.
- Keep the spec self-contained. A reader should be able to implement it
  with only the spec, the template, and the repo — no chat history.

## When done

Stop. The user triggers implementation separately.
