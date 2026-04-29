using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Drover.App.Models;

public sealed record ProjectDefinition(
    string Name,
    string Path,
    ProjectKind Kind,
    string? Command = null,
    string? Args = null,
    Dictionary<string, string>? EnvVars = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? PlansFolder = null,
    /// <summary>Hex color (e.g. "#3D7EFF") applied as the tab header background. Null = use the default chrome.</summary>
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TabColor = null);

public enum ProjectKind
{
    Claude,
    Pwsh
}
