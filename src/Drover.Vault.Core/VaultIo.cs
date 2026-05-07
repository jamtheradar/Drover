using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Drover.Vault.Core.Models;

namespace Drover.Vault.Core;

// Stateless file-IO layer for the vault. Every public method takes a root
// + relative path, immediately resolves through VaultPaths.ResolveSafe,
// then operates on the absolute path. No watcher integration here — the
// app project's VaultStore handles the RecentWriteGuard stamp around
// these calls.
public static class VaultIo
{
    private const int SummaryMaxLength = 120;

    // Recursive *.md enumeration under <root>/<subfolder?>. Returns a flat
    // list of VaultEntry rooted at the namespace root. Non-existent root
    // returns empty (the panel uses this for its initial paint before any
    // vault file exists).
    public static IReadOnlyList<VaultEntry> List(string root, string? subfolder = null)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("root must be non-empty.", nameof(root));

        var rootFull = Path.GetFullPath(root);
        var scanRoot = string.IsNullOrEmpty(subfolder)
            ? rootFull
            : VaultPaths.ResolveSafe(rootFull, subfolder!);
        if (!Directory.Exists(scanRoot)) return Array.Empty<VaultEntry>();

        var entries = new List<VaultEntry>();
        foreach (var path in Directory.EnumerateFiles(scanRoot, "*.md", SearchOption.AllDirectories))
        {
            FileInfo info;
            try { info = new FileInfo(path); }
            catch { continue; }

            var rel = Path.GetRelativePath(rootFull, path).Replace('\\', '/');
            var summary = ReadSummary(path);
            entries.Add(new VaultEntry(rel, info.LastWriteTimeUtc, info.Length, summary));
        }

        entries.Sort((a, b) => string.Compare(a.RelativePath, b.RelativePath, StringComparison.OrdinalIgnoreCase));
        return entries;
    }

    public static string Read(string root, string relativePath)
    {
        var abs = VaultPaths.ResolveSafe(root, relativePath);
        return File.ReadAllText(abs);
    }

    // Atomic temp-file-and-rename. Power-cut between WriteAllText and Move
    // leaves either no file (write failed) or the previous version (move
    // failed) — never a partial. Creates parent directories as needed.
    public static void Write(string root, string relativePath, string content)
    {
        var abs = VaultPaths.ResolveSafe(root, relativePath);
        var dir = Path.GetDirectoryName(abs);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = abs + ".tmp";
        File.WriteAllText(tmp, content ?? string.Empty);
        try { File.Move(tmp, abs, overwrite: true); }
        catch
        {
            // Best-effort cleanup so a failed move doesn't leave a stranded
            // .tmp next to the target. Swallow any secondary error and let
            // the original Move failure propagate.
            try { File.Delete(tmp); } catch { }
            throw;
        }
    }

    // Append with a leading newline only when the existing tail isn't
    // already one. Cosmetic — avoids producing `aa\n\nbb` when the caller
    // wants `aa\nbb`. Creates the file (and parents) if missing.
    public static void Append(string root, string relativePath, string content)
    {
        var abs = VaultPaths.ResolveSafe(root, relativePath);
        var dir = Path.GetDirectoryName(abs);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        bool needsLeadingNewline = false;
        if (File.Exists(abs))
        {
            var info = new FileInfo(abs);
            if (info.Length > 0)
            {
                using var fs = new FileStream(abs, FileMode.Open, FileAccess.Read, FileShare.Read);
                fs.Seek(-1, SeekOrigin.End);
                var lastByte = fs.ReadByte();
                needsLeadingNewline = lastByte != '\n';
            }
        }

        using var append = new FileStream(abs, FileMode.Append, FileAccess.Write, FileShare.Read);
        if (needsLeadingNewline) append.WriteByte((byte)'\n');
        var bytes = Encoding.UTF8.GetBytes(content ?? string.Empty);
        append.Write(bytes, 0, bytes.Length);
    }

    // Streaming case-insensitive substring search. Caps at maxHits across
    // the whole namespace — the plan punts on indexing until vaults grow
    // past ~1k files, so File.ReadLines is fine.
    public static IReadOnlyList<VaultSearchHit> Search(string root, string query, int maxHits = 50)
    {
        if (string.IsNullOrEmpty(query)) return Array.Empty<VaultSearchHit>();
        if (maxHits <= 0) return Array.Empty<VaultSearchHit>();

        var rootFull = Path.GetFullPath(root);
        if (!Directory.Exists(rootFull)) return Array.Empty<VaultSearchHit>();

        var hits = new List<VaultSearchHit>();
        foreach (var path in Directory.EnumerateFiles(rootFull, "*.md", SearchOption.AllDirectories))
        {
            if (hits.Count >= maxHits) break;
            int lineNumber = 0;
            IEnumerable<string> lines;
            try { lines = File.ReadLines(path); }
            catch { continue; }

            foreach (var line in lines)
            {
                lineNumber++;
                if (line.IndexOf(query, StringComparison.OrdinalIgnoreCase) < 0) continue;

                var rel = Path.GetRelativePath(rootFull, path).Replace('\\', '/');
                hits.Add(new VaultSearchHit(rel, lineNumber, Trim(line, 200)));
                if (hits.Count >= maxHits) break;
            }
        }
        return hits;
    }

    // First non-empty, non-frontmatter line, trimmed to SummaryMaxLength.
    // Frontmatter is the leading `---\n…\n---\n` block per the rest of the
    // app; we skip lines until we exit it. Files with no frontmatter and
    // no body return null.
    private static string? ReadSummary(string path)
    {
        try
        {
            using var sr = new StreamReader(path);
            bool inFrontmatter = false;
            bool sawAnyLine = false;
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                if (!sawAnyLine)
                {
                    sawAnyLine = true;
                    if (line.Trim() == "---") { inFrontmatter = true; continue; }
                }
                if (inFrontmatter)
                {
                    if (line.Trim() == "---") { inFrontmatter = false; continue; }
                    continue;
                }
                if (string.IsNullOrWhiteSpace(line)) continue;
                return Trim(line.TrimStart('#', ' ', '\t'), SummaryMaxLength);
            }
        }
        catch { /* unreadable file → null summary */ }
        return null;
    }

    private static string Trim(string s, int max)
        => s.Length <= max ? s : s.Substring(0, max);
}
