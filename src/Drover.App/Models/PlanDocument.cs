namespace Drover.App.Models;

public sealed record PlanTask(
    int LineNumber,
    string Text,
    bool IsDone,
    int IndentLevel);

public sealed record PlanSection(
    int LineNumber,
    int HeadingLevel,
    string Heading,
    string Summary,
    System.Collections.Generic.IReadOnlyList<PlanTask> Tasks);

public sealed record PlanDocument(
    string FilePath,
    bool Exists,
    string RawMarkdown,
    System.Collections.Generic.IReadOnlyList<PlanSection> Sections,
    System.Collections.Generic.IReadOnlyList<PlanTask> OrphanTasks);
