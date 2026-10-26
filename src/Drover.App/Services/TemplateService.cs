using System.IO;
using System.Reflection;

namespace Drover.App.Services;

/// <summary>
/// Lazily copies the embedded markdown templates (spec-template, prompt-enhance,
/// prompt-promote-interrogate) into a project's <c>.drover/</c> directory on first
/// use, plus starter subagent definitions into <c>.claude/agents/</c>. Once copied
/// the files are user-editable; Drover never re-writes them.
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
    /// the prompt files into place, and seeds <c>.claude/agents/</c> with starter
    /// implementer/reviewer subagents so <c>agent:</c> / <c>verify_with:</c> and the
    /// specs panel's <em>Run with agent</em> have something to resolve against.
    /// Idempotent — won't overwrite existing files.
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
    private const string PromoteGroupPromptName = "prompt-promote-group.md";
    private const string ExecuteSpecPromptName = "prompt-execute-spec.md";
    private const string LoopBatchPromptName = "prompt-loop-batch.md";
    private const string PlanToTasksPromptName = "prompt-plan-to-tasks.md";

    // Starter subagents. AgentCatalog discovers these by scanning
    // <project>/.claude/agents/*.md, so the destination name is the agent's
    // frontmatter `name:` — not the embedded resource's file name.
    private const string ClaudeFolder = ".claude";
    private const string AgentsFolder = "agents";
    private const string ImplementerAgentResource = "agent-spec-implementer.md";
    private const string ImplementerAgentName = "spec-implementer.md";
    private const string ReviewerAgentResource = "agent-spec-reviewer.md";
    private const string ReviewerAgentName = "spec-reviewer.md";

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

        var promoteGroupPath = Path.Combine(promptsDir, PromoteGroupPromptName);
        if (!File.Exists(promoteGroupPath))
            CopyEmbedded(PromoteGroupPromptName, promoteGroupPath);

        var executePath = Path.Combine(promptsDir, ExecuteSpecPromptName);
        if (!File.Exists(executePath))
            CopyEmbedded(ExecuteSpecPromptName, executePath);

        var loopBatchPath = Path.Combine(promptsDir, LoopBatchPromptName);
        if (!File.Exists(loopBatchPath))
            CopyEmbedded(LoopBatchPromptName, loopBatchPath);

        var planToTasksPath = Path.Combine(promptsDir, PlanToTasksPromptName);
        if (!File.Exists(planToTasksPath))
            CopyEmbedded(PlanToTasksPromptName, planToTasksPath);

        InitializeAgents(projectRoot);
    }

    /// <summary>
    /// Seeds <c>&lt;projectRoot&gt;/.claude/agents/</c> with the starter implementer
    /// and reviewer subagents. Kept separate from the <c>.drover/</c> work because
    /// this directory isn't Drover's: an existing agent of the same name is a
    /// user's own, so it's left alone.
    /// </summary>
    private void InitializeAgents(string projectRoot)
    {
        var agentsDir = Path.Combine(projectRoot, ClaudeFolder, AgentsFolder);

        var implementerPath = Path.Combine(agentsDir, ImplementerAgentName);
        if (!File.Exists(implementerPath))
            CopyEmbedded(ImplementerAgentResource, implementerPath);

        var reviewerPath = Path.Combine(agentsDir, ReviewerAgentName);
        if (!File.Exists(reviewerPath))
            CopyEmbedded(ReviewerAgentResource, reviewerPath);
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
