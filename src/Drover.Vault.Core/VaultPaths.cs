using System;
using System.IO;

namespace Drover.Vault.Core;

// Path helpers + containment guard. ResolveSafe is the choke point —
// every caller (VaultIo, VaultStore, the future MCP exe) routes through
// it before touching the filesystem so a relative path with `..`
// segments can't escape the vault root.
public static class VaultPaths
{
    private const string ProjectVaultSubdir = ".drover/vault";

    public static string GetGlobalRoot()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "Drover", "vault");
    }

    public static string GetProjectRoot(string projectPath)
    {
        if (string.IsNullOrWhiteSpace(projectPath))
            throw new ArgumentException("projectPath must be non-empty.", nameof(projectPath));
        return Path.Combine(projectPath, ".drover", "vault");
    }

    // Returns the absolute, fully-resolved path under <root> for a caller-supplied
    // relative path. Throws VaultPathException if the result escapes <root> (via
    // `..` segments, an absolute path masquerading as relative, etc.) or if the
    // input is empty / null. The root itself is allowed to not exist yet —
    // callers create directories on write.
    public static string ResolveSafe(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new ArgumentException("root must be non-empty.", nameof(root));
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new VaultPathException("Relative path must be non-empty.");

        var rootFull = Path.GetFullPath(root);
        // Append a trailing separator so the StartsWith check correctly rejects
        // siblings that happen to share a prefix (e.g. `vault` vs `vault-evil`).
        var rootWithSep = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        // Reject inputs that already look absolute — the API contract is
        // root-relative only, and accepting them would silently bypass the
        // containment check below in some edge cases.
        if (Path.IsPathRooted(relativePath))
            throw new VaultPathException($"Path must be relative to the vault root: '{relativePath}'.");

        string combined;
        try { combined = Path.GetFullPath(Path.Combine(rootFull, relativePath)); }
        catch (Exception ex)
        {
            throw new VaultPathException($"Invalid relative path '{relativePath}'.", ex);
        }

        if (!combined.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase)
            && !string.Equals(combined, rootFull, StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultPathException($"Resolved path escapes the vault root: '{relativePath}'.");
        }

        return combined;
    }
}

public sealed class VaultPathException : Exception
{
    public VaultPathException(string message) : base(message) { }
    public VaultPathException(string message, Exception inner) : base(message, inner) { }
}
