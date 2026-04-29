using System.Linq;
using System.Text;
using Drover.App.Models;

namespace Drover.App.Services;

public static class PlanPromptBuilder
{
    public static string ForTask(PlanTask task, PlanSection? owningSection, string planFile)
    {
        var sb = new StringBuilder();
        sb.Append("Implement this task from ").Append(planFile).Append(":\n\n");
        sb.Append(task.Text).Append("\n\n");
        if (owningSection is not null)
        {
            sb.Append("Context — this task is under section \"").Append(owningSection.Heading).Append("\":\n");
            if (!string.IsNullOrWhiteSpace(owningSection.Summary))
                sb.Append(owningSection.Summary).Append('\n');
            sb.Append('\n');
        }
        sb.Append("When done, mark the checkbox `[x]` in ").Append(planFile)
          .Append(" and add a one-line note explaining what was changed. ")
          .Append("Use the Task tool for subagents if helpful.");
        return sb.ToString();
    }

    public static string ForSection(PlanSection section, string planFile)
    {
        var sb = new StringBuilder();
        sb.Append("Implement section \"").Append(section.Heading).Append("\" from ").Append(planFile).Append(".\n\n");
        sb.Append("Section summary:\n");
        sb.Append(string.IsNullOrWhiteSpace(section.Summary) ? "(no summary)" : section.Summary).Append("\n\n");
        var undone = section.Tasks.Where(t => !t.IsDone).ToList();
        sb.Append("Tasks (").Append(undone.Count).Append(" remaining of ").Append(section.Tasks.Count).Append("):\n");
        foreach (var t in undone)
            sb.Append("- ").Append(t.Text).Append('\n');
        sb.Append('\n');
        sb.Append("Work through the tasks. Mark each `[x]` in ").Append(planFile).Append(" as you complete it. ")
          .Append("Use the Task tool for subagents if helpful. When the section is fully done, ")
          .Append("add a brief \"done\" note at the end of the section.");
        return sb.ToString();
    }

    public static string ForReadAndCreateTasks(string planFile)
    {
        return
            $"Read {planFile} in this project. For each section, ensure the task list is\n" +
            "well-formed:\n\n" +
            "- Each task should be a single concrete action, not a vague goal.\n" +
            "- Add any tasks implied by the section summary that aren't yet listed.\n" +
            "- Don't add tasks for work that's clearly out of scope.\n" +
            "- Don't reorder existing tasks.\n" +
            "- Don't change task text that's already concrete.\n" +
            "- Use `- [ ]` for new tasks. Preserve existing `[x]` checkboxes exactly.\n\n" +
            $"After updating, save the file. Don't run any code or make any changes\n" +
            $"outside {planFile}.";
    }

    public const string DefaultTemplate =
        "# {ProjectName} — Plan\n\n" +
        "Living plan for this project. Drover reads this file and surfaces phases and\n" +
        "tasks in the right panel. Click any task or phase to send a canned\n" +
        "implementation prompt to the active session.\n\n" +
        "## Phase 1 — Setup\n\n" +
        "Initial scaffolding and exploration.\n\n" +
        "- [ ] Describe the first piece of work here\n\n" +
        "## Phase 2 — Build\n\n" +
        "The main delivery.\n\n" +
        "- [ ] Add tasks as they become concrete\n\n" +
        "## Phase 3 — Polish\n\n" +
        "Hardening and rough edges.\n\n" +
        "- [ ] Add tasks here when in scope\n";
}
