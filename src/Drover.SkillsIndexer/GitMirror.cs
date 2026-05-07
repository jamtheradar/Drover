using System.Diagnostics;

namespace Drover.SkillsIndexer;

// Wraps `git` for the clone/refresh dance. We assume git is on PATH; if it's
// not we surface that to the caller as an exception rather than silently
// leaving the mirror empty.
//
// Strategy:
// - First run: shallow clone (`--depth 1`) — we only ever need HEAD.
// - Subsequent runs: `git fetch --depth 1 origin <branch>` + `reset --hard FETCH_HEAD`.
//   Cheap, ignores any local changes (there shouldn't be any), and leaves the
//   working tree at the latest commit.
internal static class GitMirror
{
    public sealed record MirrorResult(string LocalPath, string Branch, string CommitSha);

    public static async Task<MirrorResult> EnsureAsync(string source, string mirrorRoot, CancellationToken ct)
    {
        var (owner, repo, branchOverride) = ParseSource(source);
        var localPath = Path.Combine(mirrorRoot, $"{owner}-{repo}");
        var url = $"https://github.com/{owner}/{repo}.git";

        if (!Directory.Exists(Path.Combine(localPath, ".git")))
        {
            Directory.CreateDirectory(mirrorRoot);
            if (Directory.Exists(localPath)) Directory.Delete(localPath, recursive: true);
            var args = branchOverride is null
                ? $"clone --depth 1 \"{url}\" \"{localPath}\""
                : $"clone --depth 1 --branch {branchOverride} \"{url}\" \"{localPath}\"";
            await RunAsync("git", args, workingDir: mirrorRoot, ct).ConfigureAwait(false);
        }
        else
        {
            var branch = branchOverride ?? await GetBranchAsync(localPath, ct).ConfigureAwait(false);
            await RunAsync("git", $"fetch --depth 1 origin {branch}", localPath, ct).ConfigureAwait(false);
            await RunAsync("git", "reset --hard FETCH_HEAD", localPath, ct).ConfigureAwait(false);
        }

        var resolvedBranch = branchOverride ?? await GetBranchAsync(localPath, ct).ConfigureAwait(false);
        var sha = (await CaptureAsync("git", "rev-parse HEAD", localPath, ct).ConfigureAwait(false)).Trim();
        return new MirrorResult(localPath, resolvedBranch, sha);
    }

    private static (string Owner, string Repo, string? Branch) ParseSource(string source)
    {
        var s = source.Trim();
        string? branch = null;
        var at = s.IndexOf('@');
        if (at > 0) { branch = s[(at + 1)..]; s = s[..at]; }
        var parts = s.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) throw new InvalidOperationException($"Source '{source}' is not in 'owner/repo' form.");
        return (parts[0], parts[1], branch);
    }

    private static async Task<string> GetBranchAsync(string repoDir, CancellationToken ct)
    {
        var name = (await CaptureAsync("git", "rev-parse --abbrev-ref HEAD", repoDir, ct).ConfigureAwait(false)).Trim();
        return string.IsNullOrEmpty(name) || name == "HEAD" ? "main" : name;
    }

    private static async Task RunAsync(string file, string args, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start: {file} {args}");
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0)
        {
            var stderr = await p.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
            throw new InvalidOperationException($"git failed ({p.ExitCode}): {stderr.Trim()}");
        }
    }

    private static async Task<string> CaptureAsync(string file, string args, string workingDir, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file, args)
        {
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException($"Failed to start: {file} {args}");
        var stdout = await p.StandardOutput.ReadToEndAsync(ct).ConfigureAwait(false);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        return stdout;
    }
}
