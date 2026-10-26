# Drover prompt: plan-to-tasks

You are turning a plan document into a concrete, actionable task list.

- Plan file: `{{PLAN_FILE}}`
- Task list file: `{{TASKS_FILE}}`

(Drover injects both paths. If either placeholder is still literal text,
ask the user which files they meant and stop.)

## What to do

1. Read the plan file end to end. It describes *intent* — phases,
   rationale, design decisions.
2. Read the task list file if it already exists. You are appending to it,
   not rewriting it. Never edit or reorder existing lines, and never
   change an existing `[x]`.
3. For each plan section that still has unfinished work, derive tasks:
   - One concrete action per line, in the form `- [ ] <action>`.
   - Small enough that a single session can finish it; big enough to be
     worth a line. If a plan bullet is really three actions, split it.
   - Name the files or components involved when the plan makes them
     obvious. A task a stranger can't start is not a task yet.
   - Skip anything the plan marks done, or that is already covered by an
     existing line in the task list.
4. Group the new tasks under a `## ` heading naming the plan section they
   came from, so the provenance survives. If a matching heading already
   exists in the task list, append under it instead of duplicating it.
5. Write the file. Then report back: how many tasks you added, under
   which headings, and anything in the plan you deliberately skipped and
   why.

## Boundaries

- Touch only the task list file. Do not modify the plan, and do not
  implement anything.
- Don't invent scope. If the plan is vague in a place that matters, add
  the task anyway but flag the ambiguity in your report rather than
  guessing at a design.

## What happens next

Each task line can be promoted to a full spec from Drover's Tasks panel
(right-click a row ▸ *Promote (interrogate)*), which interrogates the
user and writes `specs/{NNNN}-{slug}.md`. Specs are what the run loop
executes. Keep that in mind: a good task line is one that would make a
good spec title.
