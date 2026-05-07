# Drover prompt: promote-with-interrogation

You are helping the user turn a one-line entry from `tasks.md` into a
fully-formed spec under `specs/`. The line you're working from is:

> {{TASK_LINE}}

(Drover injects the literal task line here. If the placeholder is still
present, ask the user which line in tasks.md they meant and stop.)

## What to do

1. Read `.drover/spec-template.md`. This is the scaffold. Don't deviate
   from its structure.
2. Read `tasks.md` to confirm the line is still present and grab any
   surrounding context (section heading, sibling tasks).
3. List existing specs under `specs/` to determine the next spec id
   (zero-padded, 4 digits, monotonically increasing — e.g., if the
   highest is `0007-foo.md`, the new id is `0008`).
4. Do a quick repo grep to identify the *probable* affected files for
   this task. Don't be exhaustive; aim for the obvious 1-3 files.
5. Ask the user **at most 4** clarifying questions, one at a time.
   Choose from gaps you genuinely have after steps 2-4. Don't ask
   questions whose answers are already clear. Likely areas:
   - "I think the affected files are X and Y — anything else?"
   - "What's the acceptance criterion that would convince you this is
     done?"
   - "Anything explicitly out of scope here?"
   - "Edge cases or failure modes I should call out?"
   - "Are there other specs this depends on or blocks?"
   - "I see this change touches a shared {HttpClient registration / DI
     singleton / build property / config section}, which is also used
     by {list of other consumers}. Should the change apply to all of
     them, or be scoped to one?" — only ask if you actually find a
     shared registration the change would affect; this catches the
     out-of-scope-but-still-affected case where adding policy to one
     consumer silently changes behaviour for siblings.

   If the task is genuinely tiny and unambiguous, ask zero questions and
   proceed. Don't manufacture questions to look thorough.

6. Generate `specs/{NNNN}-{slug}.md` from the template, where:
   - `{NNNN}` is the next id
   - `{slug}` is a kebab-case version of the task title, max 5 words
   - frontmatter `status` is `pending`
   - frontmatter `created` is today's date in YYYY-MM-DD
   - All template sections are filled in. No section may be left as
     placeholder text. If a section truly doesn't apply, write "N/A —
     {reason}" rather than deleting it.
7. Show the user the proposed spec content. Wait for explicit approval
   before writing.
8. On approval, write the file and confirm in one line: "Created
   specs/{NNNN}-{slug}.md. Status: pending."

## Constraints

- Do **not** start implementing the spec. This action only creates the
  spec file. Implementation happens via the loop runner.
- Do **not** edit `tasks.md`. The user decides whether to remove the
  promoted line themselves.
- Do **not** create the spec if the user's answers reveal the task is
  actually two or more specs. In that case, stop and tell the user:
  "This looks like N specs to me: [list]. Want me to create them
  separately?"
- Keep the spec self-contained. A reader should be able to implement it
  with only the spec, the template, and the repo — no chat history.

## When done

Stop. The user triggers implementation separately.
