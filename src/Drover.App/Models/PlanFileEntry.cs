namespace Drover.App.Models;

/// <summary>
/// One discoverable plan source. Identity is the absolute path on disk.
/// `IsDone` reflects file location (under `<plansFolder>/done/`) rather than
/// any in-file marker — completion is encoded by where the file lives.
/// `IsRoot` is true only for `<projectRoot>/PLAN.md`; the UI uses this to
/// disable the "Mark done" button on the root plan (it's the bootstrap).
/// </summary>
public sealed record PlanFileEntry(
    string AbsolutePath,
    string RelativePath,
    string DisplayName,
    bool IsDone,
    bool IsRoot);
