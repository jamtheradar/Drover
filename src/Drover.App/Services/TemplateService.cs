using System.IO;
using System.Reflection;

namespace Drover.App.Services;

/// <summary>
/// Lazily copies the embedded markdown templates (spec-template, prompt-enhance,
/// prompt-promote-interrogate) into a project's <c>.drover/</c> directory on first
/// use. Once copied the files are user-editable; Drover never re-writes them.
/// </summary>
public interface ITemplateService
{
    /// <summary>
    /// Ensures <c>.drover/spec-template.md</c> exists for the given project root.
    /// Copies the embedded default if missing. Returns the absolute path.
    /// </summary>
    string EnsureSpecTemplate(string projectRoot);

    /// <summary>
    /// True if <c>.drover/</c> exists and contains <c>spec-template.md</c>.
    /// Used to decide whether to show "Initialize specs" vs other actions.
    /// </summary>
    bool IsInitialized(string projectRoot);

    /// <summary>
    /// Initializes <c>.drover/</c> for a project: copies <c>spec-template.md</c> and
    /// the two prompt files into place. Idempotent — won't overwrite existing files.
    /// </summary>
    void Initialize(string projectRoot);
}

public sealed class TemplateService : ITemplateService
{
    private const string DroverFolder = ".drover";
    private const string PromptsFolder = "prompts";
    private const string SpecTemplateName = "spec-template.md";
    private const string EnhancePromptName = "prompt-enhance-template.md";
    private const string PromotePromptName = "prompt-promote-interrogate.md";
    private const string ExecuteSpecPromptName = "prompt-execute-spec.md";
    private const string LoopBatchPromptName = "prompt-loop-batch.md";

    // Resource names follow .NET's <DefaultNamespace>.<Folder>.<File> convention.
    private const string ResourcePrefix = "Drover.App.Templates.";

    private readonly Assembly _assembly;

    public TemplateService()
    {
        _assembly = typeof(TemplateService).Assembly;
    }

    public bool IsInitialized(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot)) return false;
        return File.Exists(Path.Combine(projectRoot, DroverFolder, SpecTemplateName));
    }

    public string EnsureSpecTemplate(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot))
            throw new System.ArgumentException("projectRoot is required", nameof(projectRoot));
        var path = Path.Combine(projectRoot, DroverFolder, SpecTemplateName);
        if (!File.Exists(path))
            CopyEmbedded(SpecTemplateName, path);
        return path;
    }

    public void Initialize(string projectRoot)
    {
        if (string.IsNullOrEmpty(projectRoot))
            throw new System.ArgumentException("projectRoot is required", nameof(projectRoot));

        var droverDir = Path.Combine(projectRoot, DroverFolder);
        var promptsDir = Path.Combine(droverDir, PromptsFolder);

        var specPath = Path.Combine(droverDir, SpecTemplateName);
        if (!File.Exists(specPath))
            CopyEmbedded(SpecTemplateName, specPath);

        var enhancePath = Path.Combine(promptsDir, EnhancePromptName);
        if (!File.Exists(enhancePath))
            CopyEmbedded(EnhancePromptName, enhancePath);

        var promotePath = Path.Combine(promptsDir, PromotePromptName);
        if (!File.Exists(promotePath))
            CopyEmbedded(PromotePromptName, promotePath);

        var executePath = Path.Combine(promptsDir, ExecuteSpecPromptName);
        if (!File.Exists(executePath))
            CopyEmbedded(ExecuteSpecPromptName, executePath);

        var loopBatchPath = Path.Combine(promptsDir, LoopBatchPromptName);
        if (!File.Exists(loopBatchPath))
            CopyEmbedded(LoopBatchPromptName, loopBatchPath);
    }

    /// <summary>
    /// Reads the embedded resource by file name and writes it to <paramref name="destPath"/>,
    /// creating parent directories as needed. Throws when the resource is missing — that's
    /// a build-config bug, not a runtime condition we can recover from.
    /// </summary>
    internal void CopyEmbedded(string fileName, string destPath)
    {
        var dir = Path.GetDirectoryName(destPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var resourceName = ResourcePrefix + fileName;
        using var stream = _assembly.GetManifestResourceStream(resourceName)
            ?? throw new System.InvalidOperationException(
                $"Embedded template '{resourceName}' not found. Check Drover.App.csproj <EmbeddedResource> entries.");
        using var fs = new FileStream(destPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.CopyTo(fs);
    }

    internal Stream OpenEmbedded(string fileName)
    {
        var resourceName = ResourcePrefix + fileName;
        return _assembly.GetManifestResourceStream(resourceName)
            ?? throw new System.InvalidOperationException(
                $"Embedded template '{resourceName}' not found.");
    }
}
