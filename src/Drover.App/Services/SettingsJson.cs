using System.Text.Encodings.Web;
using System.Text.Json;

namespace Drover.App.Services;

/// <summary>
/// Shared serializer options for the JSON settings files Drover writes
/// (<c>.claude/settings.local.json</c>, <c>.mcp.json</c>, Codex hooks).
/// The default System.Text.Json encoder HTML-escapes quotes and other
/// characters (<c>"</c> becomes <c>"</c>), which makes hook command
/// strings unreadable in the file. UnsafeRelaxedJsonEscaping keeps them
/// literal — safe here because the output is only ever consumed by JSON
/// parsers, never embedded in HTML.
/// </summary>
internal static class SettingsJson
{
    public static readonly JsonSerializerOptions Indented = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}
