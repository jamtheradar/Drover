namespace Drover.App.Services;

/// <summary>
/// Source of attention-state signals for a single tab. Today AttentionMonitor
/// (OSC-title scraping) implements this. v0.2 will add a hooks-based source
/// that fires on real Claude lifecycle/tool events. Both feed the same channel.
/// </summary>
public interface IAttentionSource
{
    AttentionState State { get; }
    event EventHandler<AttentionState>? StateChanged;
}
