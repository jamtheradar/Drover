# Drover prompt: enhance-spec-template

You are helping the user tailor `.drover/spec-template.md` to this specific
project. The template is the scaffold every new spec inherits from, so
getting it right pays off many times.

## What to do

1. Read `.drover/spec-template.md`. If it doesn't exist, stop and tell the
   user to run "Initialize Drover" first.
2. Read `CLAUDE.md` if it exists, plus the top-level project files
   (solution file, top-level package manifests, README) so you understand
   the stack and conventions.
3. Ask the user **at most 5** clarifying questions, one message at a time
   (wait for each answer before asking the next). Pick the questions that
   would most change the template for *this* project. Suggested areas:
   - Primary tech stack and language conventions worth baking in
   - The exact test command(s) and what "green" means here
   - Code review conventions — branch naming, commit message style, PR
     checklist items
   - Deployment targets that affect acceptance criteria (e.g., iOS App
     Store readiness, NuGet publish, Azure Function deploy)
   - Project-specific "always" and "never" rules (e.g., "never edit
     generated files in /obj", "always update CHANGELOG.md")

   Don't ask questions whose answers are already obvious from the files
   you read. Don't pad to 5 if 3 will do.

4. After the user answers, propose the new `.drover/spec-template.md` as
   a unified diff against the current version. Wait for explicit approval
   ("yes", "save it", "looks good") before writing.
5. On approval:
   - Copy the current file to
     `.drover/history/spec-template.{YYYYMMDD-HHmmss}.md`, where the
     timestamp is the current local time. Create `.drover/history/` if
     it doesn't exist.
   - Write the new template to `.drover/spec-template.md`.
   - Confirm in one line: "Template updated. Previous version archived
     at .drover/history/spec-template.{YYYYMMDD-HHmmss}.md."

## Constraints

- Keep the template's frontmatter schema unchanged: `id`, `title`,
  `status`, `created`, `depends_on`, `estimate`. Drover parses these.
  You may add new frontmatter fields, but don't remove or rename the
  existing ones.
- Keep the section headings `## Context`, `## Affected files`,
  `## Acceptance criteria`, `## Test plan`. Drover may surface these in
  the UI later. Other sections are free to add, reorder, or remove.
- Don't bake answers in as hard-coded text where a placeholder belongs.
  If the test command is `dotnet test`, the template's Test plan section
  should *suggest* `dotnet test` as the default, not assert it as the
  only option.
- The whole template must stay under ~120 lines. If you're tempted to go
  longer, you're putting project documentation in the wrong place — that
  belongs in CLAUDE.md.

## When done

Stop. Do not start working on any specs. The user will trigger the next
action from Drover.
