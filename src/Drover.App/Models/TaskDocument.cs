namespace Drover.App.Models;

public sealed record TaskItem(
    int LineNumber,
    string Text,
    bool IsDone,
    int IndentLevel);

public sealed record TaskSection(
    int LineNumber,
    int HeadingLevel,
    string Heading,
    string Summary,
    System.Collections.Generic.IReadOnlyList<TaskItem> Items);

public sealed record TaskDocument(
    string FilePath,
    bool Exists,
    string RawMarkdown,
    System.Collections.Generic.IReadOnlyList<TaskSection> Sections,
    System.Collections.Generic.IReadOnlyList<TaskItem> OrphanItems);
