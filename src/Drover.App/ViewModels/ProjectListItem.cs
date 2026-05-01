using CommunityToolkit.Mvvm.ComponentModel;
using Drover.App.Models;

namespace Drover.App.ViewModels;

/// <summary>
/// Sidebar/welcome list wrapper for a <see cref="ProjectDefinition"/>. Adds the
/// reactive git-status chip on top of the underlying record (records are immutable,
/// so the chip can't live on the model itself). Forwards <c>Name</c> / <c>Path</c> /
/// <c>TabColor</c> so existing XAML bindings keep working unchanged.
/// </summary>
public sealed partial class ProjectListItem : ObservableObject
{
    public ProjectDefinition Project { get; }

    [ObservableProperty] private string _gitText = string.Empty;
    [ObservableProperty] private bool _gitDirty;
    [ObservableProperty] private bool _gitHasRepo;

    public ProjectListItem(ProjectDefinition project)
    {
        Project = project;
    }

    public string Name => Project.Name;
    public string Path => Project.Path;
    public string? TabColor => Project.TabColor;
    public ProjectKind Kind => Project.Kind;
}
