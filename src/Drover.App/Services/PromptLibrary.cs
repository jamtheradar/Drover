using System.IO;

namespace Drover.App.Services;

/// <summary>
/// Resolves canned prompt text for the spec-driven workflow. A per-project
/// override (<c>{projectRoot}/.drover/prompts/{name}.md</c>) wins over the
/// embedded default. The override mechanism is the user's escape hatch:
/// behaviour they want to customise lives in the markdown they edit, not in
/// C#.
/// </summary>
public interface IPromptLibrary
{
    /// <summary>
    /// Resolve a prompt by name. Checks
    /// <c>{projectRoot}/.drover/prompts/{name}.md</c> first, then falls back
    /// to the embedded default. Throws if neither exists.
    ///
    /// Substitutes <c>{{TOKEN}}</c> placeholders from the tokens dictionary
    /// using a single <see cref="string.Replace(string, string)"/> per token.
    /// Tokens not present in the prompt are silently ignored. Tokens present
    /// in the prompt but not supplied are left as literal <c>{{TOKEN}}</c>
    /// text — the prompt itself instructs Claude what to do in that case.
    /// </summary>
    string Resolve(string name, string projectRoot,
                   IDictionary<string, string>? tokens = null);
}

public sealed class PromptLibrary : IPromptLibrary
{
    private const string DroverFolder = ".drover";
    private const string PromptsFolder = "prompts";

    private readonly TemplateService _templates;

    /// <summary>
    /// Maps the public prompt name (without extension) to the embedded resource
    /// file name. Adding a new prompt? Add it to <c>Templates/</c>, register it
    /// here, and the resolver picks up both the embedded default and the
    /// per-project override automatically.
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<string, string> KnownPrompts =
        new(System.StringComparer.OrdinalIgnoreCase)
        {
            ["enhance-template"] = "prompt-enhance-template.md",
            ["promote-interrogate"] = "prompt-promote-interrogate.md",
            ["execute-spec"] = "prompt-execute-spec.md",
            ["loop-batch"] = "prompt-loop-batch.md",
        };

    public PromptLibrary(TemplateService templates)
    {
        _templates = templates;
    }

    public string Resolve(string name, string projectRoot, IDictionary<string, string>? tokens = null)
    {
        if (string.IsNullOrEmpty(name))
            throw new System.ArgumentException("name is required", nameof(name));

        if (!KnownPrompts.TryGetValue(name, out var fileName))
            throw new System.ArgumentException($"Unknown prompt: '{name}'", nameof(name));

        var body = ReadProjectOverride(projectRoot, fileName) ?? ReadEmbedded(fileName);

        if (tokens is not null)
        {
            foreach (var kvp in tokens)
            {
                if (string.IsNullOrEmpty(kvp.Key)) continue;
                var placeholder = "{{" + kvp.Key + "}}";
                body = body.Replace(placeholder, kvp.Value ?? string.Empty);
            }
        }

        return body;
    }

    private static string? ReadProjectOverride(string projectRoot, string fileName)
    {
        if (string.IsNullOrEmpty(projectRoot)) return null;
        var path = Path.Combine(projectRoot, DroverFolder, PromptsFolder, fileName);
        if (!File.Exists(path)) return null;
        try { return File.ReadAllText(path); }
        catch { return null; }
    }

    private string ReadEmbedded(string fileName)
    {
        using var stream = _templates.OpenEmbedded(fileName);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
