namespace Drover.App.Models;

/// <summary>
/// One discoverable task list source. Identity is the absolute path on disk.
/// `IsDone` reflects file location (under `<tasksFolder>/done/`) rather than
/// any in-file marker — completion is encoded by where the file lives.
/// `IsRoot` is true only for `<projectRoot>/TASKS.md`; the UI uses this to
/// disable the "Mark done" button on the root list (it's the bootstrap).
/// </summary>
public sealed record TaskFileEntry(
    string AbsolutePath,
    string RelativePath,
    string DisplayName,
    bool IsDone,
    bool IsRoot);
