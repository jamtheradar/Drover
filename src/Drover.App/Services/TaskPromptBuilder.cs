using System.Linq;
using System.Text;
using Drover.App.Models;

namespace Drover.App.Services;

public static class TaskPromptBuilder
{
    public static string ForItem(TaskItem item, TaskSection? owningSection, string taskFile)
    {
        var sb = new StringBuilder();
        sb.Append("Do this task from ").Append(taskFile).Append(":\n\n");
        sb.Append(item.Text).Append("\n\n");
        if (owningSection is not null)
        {
            sb.Append("Context — this is under section \"").Append(owningSection.Heading).Append("\":\n");
            if (!string.IsNullOrWhiteSpace(owningSection.Summary))
                sb.Append(owningSection.Summary).Append('\n');
            sb.Append('\n');
        }
        sb.Append("When done, mark the checkbox `[x]` in ").Append(taskFile).Append(".");
        return sb.ToString();
    }

    public static string ForSection(TaskSection section, string taskFile)
    {
        var sb = new StringBuilder();
        sb.Append("Work through section \"").Append(section.Heading).Append("\" from ").Append(taskFile).Append(".\n\n");
        if (!string.IsNullOrWhiteSpace(section.Summary))
            sb.Append("Section summary:\n").Append(section.Summary).Append("\n\n");
        var undone = section.Items.Where(t => !t.IsDone).ToList();
        sb.Append("Tasks (").Append(undone.Count).Append(" remaining of ").Append(section.Items.Count).Append("):\n");
        foreach (var t in undone)
            sb.Append("- ").Append(t.Text).Append('\n');
        sb.Append('\n');
        sb.Append("Mark each `[x]` in ").Append(taskFile).Append(" as you complete it.");
        return sb.ToString();
    }

    public const string DefaultTemplate =
        "# {ProjectName} — Tasks\n\n" +
        "Lightweight task list for this project. Drover surfaces sections and items in the\n" +
        "right panel — click any item or heading to send it to the active session.\n\n" +
        "## Inbox\n\n" +
        "Things to do that don't yet belong to a larger plan.\n\n" +
        "- [ ] First task\n";
}
