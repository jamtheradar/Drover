using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Drover.App.Services;

/// <summary>
/// "Open this folder in Drover" plumbing: `drover .`, `Drover.App.exe C:\repo`, and the
/// Explorer context-menu entry all land here.
///
/// The first Drover instance owns a per-user named pipe. A later launch carrying a folder
/// argument hands the path to that instance over the pipe and exits, so the folder opens
/// as a tab in the window the user already has. A launch with no folder argument is
/// unaffected — running a second Drover deliberately still works.
/// </summary>
public static class FolderLaunch
{
    private static string PipeName =>
        $"Drover-Open-{Environment.UserName}{(AppInfo.IsBeta ? "-beta" : "")}";

    /// <summary>Folder named on the command line, or null. "." and relative paths resolve
    /// against the launching process's working directory.</summary>
    public static string? ParseFolderArg(string[] args)
    {
        foreach (var a in args)
        {
            if (string.IsNullOrWhiteSpace(a) || a.StartsWith("--")) continue;
            try
            {
                var full = Path.GetFullPath(a.Trim('"'));
                if (Directory.Exists(full)) return Path.TrimEndingDirectorySeparator(full);
            }
            catch { /* not a path */ }
        }
        return null;
    }

    /// <summary>Sends <paramref name="folder"/> to a running Drover. True when one
    /// accepted it (the caller should exit).</summary>
    public static bool TryHandOff(string folder)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(400);
            var bytes = Encoding.UTF8.GetBytes(folder);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Listens for hand-offs from later launches for the life of the process.
    /// <paramref name="onFolder"/> is raised on a background thread.</summary>
    public static void StartServer(Action<string> onFolder, CancellationToken ct)
    {
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var server = new NamedPipeServerStream(PipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    var folder = (await reader.ReadToEndAsync(ct).ConfigureAwait(false)).Trim();
                    if (folder.Length > 0 && Directory.Exists(folder)) onFolder(folder);
                }
                catch (OperationCanceledException) { return; }
                catch (IOException ex)
                {
                    // Another Drover already owns the pipe (user opened a second instance).
                    // Only the first instance receives hand-offs — stop quietly.
                    AppLog.Info("FolderLaunch", $"Hand-off pipe unavailable: {ex.Message}");
                    return;
                }
                catch (Exception ex)
                {
                    AppLog.Warn("FolderLaunch", $"Hand-off listener error: {ex.Message}");
                    try { await Task.Delay(1000, ct).ConfigureAwait(false); } catch { return; }
                }
            }
        }, ct);
    }
}
