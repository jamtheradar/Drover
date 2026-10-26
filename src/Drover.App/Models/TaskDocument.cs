namespace Drover.App.Models;

public sealed record TaskItem(
    int LineNumber,
    string Text,
    bool IsDone,
    int IndentLevel,
    bool IsInProgress = false,
    string RawLine = "",
    bool IsPlanned = false) : System.ComponentModel.INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>
    /// Transient, view-only multi-select flag. A row click toggles it; the
    /// Tasks panel promotes every selected line into a single spec. Deliberately
    /// *not* a positional member — it takes no part in record equality and is
    /// never persisted. A file-watcher reload rebuilds the items and drops it,
    /// which is the intended lifetime.
    /// </summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, IsSelectedChangedArgs);
        }
    }

    private static readonly System.ComponentModel.PropertyChangedEventArgs IsSelectedChangedArgs =
        new(nameof(IsSelected));

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

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
