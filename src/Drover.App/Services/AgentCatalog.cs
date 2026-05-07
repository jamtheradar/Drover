using System.Collections.Concurrent;
using Drover.App.Models;

namespace Drover.App.Services;

/// <summary>
/// Discovers Claude Code subagents in <c>~/.claude/agents/*.md</c> (User scope) and
/// <c>&lt;projectPath&gt;/.claude/agents/*.md</c> (Project scope). Frontmatter is parsed
/// line-based — see <see cref="ParseFrontmatter"/>. FileSystemWatchers debounced at 250ms.
///
/// Project agents shadow user agents of the same name. Refreshes are idempotent.
/// </summary>
public sealed class AgentCatalog
{
    public event System.EventHandler? Changed;

    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, AgentDefinition> _userAgents = new(System.StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, AgentDefinition>> _projectAgents = new(System.StringComparer.OrdinalIgnoreCase);

    private System.IO.FileSystemWatcher? _userWatcher;
    private readonly ConcurrentDictionary<string, System.IO.FileSystemWatcher> _projectWatchers = new(System.StringComparer.OrdinalIgnoreCase);
    private System.Threading.Timer? _debounce;

    public AgentCatalog()
    {
        StartUserWatcher();
        ScanUser();
    }

    public System.Collections.Generic.IReadOnlyList<AgentDefinition> ForProject(string? projectPath)
    {
        EnsureProjectWatcher(projectPath);
        var byName = new System.Collections.Generic.Dictionary<string, AgentDefinition>(System.StringComparer.OrdinalIgnoreCase);
        foreach (var u in _userAgents.Values) byName[u.Name] = u;
        if (!string.IsNullOrEmpty(projectPath) &&
            _projectAgents.TryGetValue(projectPath!, out var proj))
        {
            foreach (var p in proj.Values) byName[p.Name] = p; // project shadows user
        }
        var list = new System.Collections.Generic.List<AgentDefinition>(byName.Values);
        list.Sort((a, b) => System.StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        return list;
    }

    public void Refresh()
    {
        ScanUser();
        foreach (var k in _projectAgents.Keys) ScanProject(k);
        Changed?.Invoke(this, System.EventArgs.Empty);
    }

    private void StartUserWatcher()
    {
        var dir = UserAgentsDir();
        if (!System.IO.Directory.Exists(dir)) return;
        try
        {
            _userWatcher = MakeWatcher(dir, () => { ScanUser(); RaiseDebounced(); });
        }
        catch { _userWatcher = null; }
    }

    private void EnsureProjectWatcher(string? projectPath)
    {
        if (string.IsNullOrEmpty(projectPath)) return;
        if (_projectWatchers.ContainsKey(projectPath!)) return;
        var dir = ProjectAgentsDir(projectPath!);
        if (!System.IO.Directory.Exists(dir))
        {
            // Pre-emptively scan so the in-memory map starts empty for this project.
            ScanProject(projectPath!);
            return;
        }
        try
        {
            var w = MakeWatcher(dir, () => { ScanProject(projectPath!); RaiseDebounced(); });
            _projectWatchers[projectPath!] = w;
        }
        catch { /* ignore */ }
        ScanProject(projectPath!);
    }

    private static System.IO.FileSystemWatcher MakeWatcher(string dir, System.Action onChange)
    {
        var w = new System.IO.FileSystemWatcher(dir, "*.md")
        {
            NotifyFilter = System.IO.NotifyFilters.LastWrite
                         | System.IO.NotifyFilters.Size
                         | System.IO.NotifyFilters.FileName
                         | System.IO.NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };
        w.Changed += (_, _) => onChange();
        w.Created += (_, _) => onChange();
        w.Deleted += (_, _) => onChange();
        w.Renamed += (_, _) => onChange();
        return w;
    }

    private void RaiseDebounced()
    {
        lock (_lock)
        {
            _debounce?.Dispose();
            _debounce = new System.Threading.Timer(_ =>
                Changed?.Invoke(this, System.EventArgs.Empty),
                null, 250, System.Threading.Timeout.Infinite);
        }
    }

    private void ScanUser()
    {
        var dir = UserAgentsDir();
        var fresh = ScanDir(dir, AgentScope.User);
        ReplaceMap(_userAgents, fresh);
    }

    private void ScanProject(string projectPath)
    {
        var dir = ProjectAgentsDir(projectPath);
        var fresh = ScanDir(dir, AgentScope.Project);
        var bucket = _projectAgents.GetOrAdd(projectPath, _ => new ConcurrentDictionary<string, AgentDefinition>(System.StringComparer.OrdinalIgnoreCase));
        ReplaceMap(bucket, fresh);
    }

    private static System.Collections.Generic.IReadOnlyDictionary<string, AgentDefinition> ScanDir(string dir, AgentScope scope)
    {
        var result = new System.Collections.Generic.Dictionary<string, AgentDefinition>(System.StringComparer.OrdinalIgnoreCase);
        if (!System.IO.Directory.Exists(dir)) return result;
        System.Collections.Generic.IEnumerable<string> files;
        try { files = System.IO.Directory.EnumerateFiles(dir, "*.md", System.IO.SearchOption.TopDirectoryOnly); }
        catch { return result; }

        foreach (var f in files)
        {
            try
            {
                var text = System.IO.File.ReadAllText(f);
                var agent = ParseFrontmatter(text, f, scope);
                if (agent is not null) result[agent.Name] = agent;
            }
            catch { /* skip malformed file */ }
        }
        return result;
    }

    private static void ReplaceMap(ConcurrentDictionary<string, AgentDefinition> dst, System.Collections.Generic.IReadOnlyDictionary<string, AgentDefinition> src)
    {
        // Replace contents in place — preserves the dict instance so concurrent readers see a coherent view.
        foreach (var key in dst.Keys)
            if (!src.ContainsKey(key)) dst.TryRemove(key, out _);
        foreach (var kv in src)
            dst[kv.Key] = kv.Value;
    }

    private static string UserAgentsDir()
    {
        var home = System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile);
        return System.IO.Path.Combine(home, ".claude", "agents");
    }

    private static string ProjectAgentsDir(string projectPath)
        => System.IO.Path.Combine(projectPath, ".claude", "agents");

    /// <summary>
    /// Line-based frontmatter parser. Reads between the first pair of <c>---</c> fences
    /// and matches <c>key: value</c> lines. Multi-line block scalars take the first line
    /// only — adequate for v1 since canonical agent files use single-line strings.
    /// </summary>
    public static AgentDefinition? ParseFrontmatter(string text, string filePath, AgentScope scope)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var lines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

        int i = 0;
        // Skip leading blanks.
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
        if (i >= lines.Length) return null;
        if (lines[i].Trim() != "---") return null;
        i++;

        string? name = null;
        string? description = null;
        string? model = null;
        var tools = new System.Collections.Generic.List<string>();

        var rx = new System.Text.RegularExpressions.Regex(@"^([A-Za-z_][A-Za-z0-9_-]*)\s*:\s*(.*)$");
        while (i < lines.Length)
        {
            var line = lines[i];
            if (line.Trim() == "---") break;
            var m = rx.Match(line);
            if (m.Success)
            {
                var key = m.Groups[1].Value;
                var val = m.Groups[2].Value.Trim();
                // Strip surrounding quotes if any.
                if (val.Length >= 2 && (val[0] == '"' || val[0] == '\'') && val[val.Length - 1] == val[0])
                    val = val.Substring(1, val.Length - 2);
                switch (key.ToLowerInvariant())
                {
                    case "name":        name = val; break;
                    case "description": description = val; break;
                    case "model":       model = string.IsNullOrEmpty(val) ? null : val; break;
                    case "tools":
                        foreach (var t in val.Split(','))
                        {
                            var trimmed = t.Trim();
                            if (!string.IsNullOrEmpty(trimmed)) tools.Add(trimmed);
                        }
                        break;
                }
            }
            i++;
        }

        if (string.IsNullOrEmpty(name)) return null;
        var id = scope == AgentScope.Project ? $"project:{name}" : $"user:{name}";
        return new AgentDefinition(id, name!, description, tools, model, filePath, scope);
    }
}
