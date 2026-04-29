using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Drover.App.Services;

public sealed record MemoryFile(string Name, string Path, string Subtitle, string SizeText, MemoryScope Scope);

public enum MemoryScope { User, Project }

/// <summary>
/// Discovers Claude Code memory files: ~/.claude/CLAUDE.md, ~/.claude/memory/*.md (user scope),
/// and any CLAUDE.md inside known project directories (project scope).
/// </summary>
public static class MemoryScanner
{
    private static readonly HashSet<string> SkipDirs = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "bin", "obj", ".vs", ".vscode", ".idea",
        "dist", "build", "out", "target", ".next", ".nuxt",
        "venv", ".venv", "env", "__pycache__", ".cache", ".gradle",
        "Pods", ".terraform", "vendor", "packages",
    };

    private const int MaxDepth = 4;
    private const int MaxFilesPerProject = 50;

    public static (IReadOnlyList<MemoryFile> User, IReadOnlyList<MemoryFile> Project) Scan(IEnumerable<string> projectPaths)
    {
        var user = new List<MemoryFile>();
        var proj = new List<MemoryFile>();

        var home = Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(home))
        {
            var claudeRoot = Path.Combine(home, ".claude");
            TryAdd(user, Path.Combine(claudeRoot, "CLAUDE.md"), "global", MemoryScope.User);
            TryAdd(user, Path.Combine(claudeRoot, "MEMORY.md"), "memory index", MemoryScope.User);
            var memDir = Path.Combine(claudeRoot, "memory");
            if (Directory.Exists(memDir))
            {
                foreach (var f in EnumerateFilesSafe(memDir, "*.md", MaxDepth))
                    TryAdd(user, f, RelativeFrom(claudeRoot, f), MemoryScope.User);
            }
            var projectsDir = Path.Combine(claudeRoot, "projects");
            if (Directory.Exists(projectsDir))
            {
                foreach (var dir in EnumerateDirsSafe(projectsDir))
                {
                    var memSub = Path.Combine(dir, "memory");
                    if (!Directory.Exists(memSub)) continue;
                    var label = Path.GetFileName(dir);
                    foreach (var f in EnumerateFilesSafe(memSub, "*.md", MaxDepth))
                        TryAdd(user, f, $"projects · {label}", MemoryScope.User);
                }
            }
        }

        foreach (var path in projectPaths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path)) continue;
            TryAdd(proj, Path.Combine(path, "CLAUDE.md"), path, MemoryScope.Project);
            int found = 0;
            foreach (var f in EnumerateFilesSafe(path, "CLAUDE.md", MaxDepth))
            {
                if (string.Equals(f, Path.Combine(path, "CLAUDE.md"), StringComparison.OrdinalIgnoreCase)) continue;
                TryAdd(proj, f, RelativeFrom(path, f), MemoryScope.Project);
                if (++found >= MaxFilesPerProject) break;
            }
        }

        return (user, proj);
    }

    /// <summary>
    /// Manual recursive walk that skips heavyweight directories (node_modules, .git, bin, obj, …)
    /// and caps depth. Vital for project roots that contain large dependency trees — a naive
    /// AllDirectories enumeration can hang for many seconds and freeze the UI.
    /// </summary>
    private static IEnumerable<string> EnumerateFilesSafe(string root, string pattern, int maxDepth)
    {
        var stack = new Stack<(string dir, int depth)>();
        stack.Push((root, 0));
        while (stack.Count > 0)
        {
            var (dir, depth) = stack.Pop();
            string[] files;
            try { files = Directory.GetFiles(dir, pattern); }
            catch { continue; }
            foreach (var f in files) yield return f;

            if (depth >= maxDepth) continue;
            string[] subs;
            try { subs = Directory.GetDirectories(dir); }
            catch { continue; }
            foreach (var sub in subs)
            {
                var name = Path.GetFileName(sub);
                if (string.IsNullOrEmpty(name)) continue;
                if (name.StartsWith('.') && !name.Equals(".claude", StringComparison.OrdinalIgnoreCase)) continue;
                if (SkipDirs.Contains(name)) continue;
                stack.Push((sub, depth + 1));
            }
        }
    }

    private static IEnumerable<string> EnumerateDirsSafe(string root)
    {
        try { return Directory.GetDirectories(root); }
        catch { return Array.Empty<string>(); }
    }

    private static void TryAdd(List<MemoryFile> list, string path, string subtitle, MemoryScope scope)
    {
        try
        {
            if (!File.Exists(path)) return;
            var fi = new FileInfo(path);
            if (list.Any(m => string.Equals(m.Path, fi.FullName, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new MemoryFile(fi.Name, fi.FullName, subtitle, FormatSize(fi.Length), scope));
        }
        catch { /* skip unreadable files */ }
    }

    private static string RelativeFrom(string root, string full)
    {
        try { return Path.GetRelativePath(root, full); } catch { return full; }
    }

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024.0 / 1024.0:0.0} MB" :
        bytes >= 1024 ? $"{bytes / 1024.0:0.0} KB" :
        $"{bytes} B";
}
