using Drover.App.Models;
using Drover.App.ViewModels;

namespace Drover.App.Services;

public enum KanbanSourceKind
{
    Local,
    Live,
    Agents,
}

/// <summary>
/// Abstraction over a card provider. KanbanViewModel doesn't care whether cards came
/// from a markdown file, a TodoWrite hook, or a frontmatter-parsed agent file —
/// the source just exposes a flat list and tells the panel whether drag is allowed.
/// </summary>
public interface IKanbanSource
{
    event System.EventHandler? CardsChanged;

    System.Collections.Generic.IReadOnlyList<KanbanCard> Cards { get; }

    /// <summary>True when MoveCardAsync is functional. Local source = true, Live/Agents = false.</summary>
    bool SupportsDrag { get; }

    /// <summary>Called when the source becomes active. <paramref name="tab"/> is null when no tab is selected.</summary>
    void Activate(TerminalTabViewModel? tab);

    /// <summary>Called when the source is replaced or the panel closes. Detach watchers/handlers here.</summary>
    void Deactivate();

    /// <summary>Persists the new status (e.g. rewrites the markdown line). Returns false if the move was rejected.</summary>
    System.Threading.Tasks.Task<bool> MoveCardAsync(KanbanCard card, KanbanStatus to);
}
