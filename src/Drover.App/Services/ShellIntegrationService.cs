using System.IO;
using Microsoft.Win32;

namespace Drover.App.Services;

/// <summary>
/// Per-user Windows shell integration, no admin needed:
///   - Explorer "Open in Drover" on folders and folder backgrounds
///     (HKCU\Software\Classes\Directory[\Background]\shell\Drover).
///   - A `drover` command: drover.cmd in %LOCALAPPDATA%\Microsoft\WindowsApps, which is on
///     PATH by default. `drover` opens the current folder, `drover path` opens path.
/// Both point at <see cref="Environment.ProcessPath"/>; Velopack installs run from a stable
/// "current" folder, so re-applying on each launch keeps them valid across updates.
/// Failures are swallowed so a locked-down machine doesn't break startup or Save.
/// </summary>
public static class ShellIntegrationService
{
    private const string MenuText = "Open in Drover";
    private static readonly string[] MenuKeys =
    {
        @"Software\Classes\Directory\shell\Drover",
        @"Software\Classes\Directory\Background\shell\Drover",
    };

    private static string ShimPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Microsoft", "WindowsApps", AppInfo.IsBeta ? "drover-beta.cmd" : "drover.cmd");

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(MenuKeys[0], writable: false);
            return key is not null || File.Exists(ShimPath);
        }
        catch { return false; }
    }

    public static void Apply(bool enabled)
    {
        var exe = Environment.ProcessPath;
        if (enabled && string.IsNullOrEmpty(exe)) return;

        foreach (var path in MenuKeys)
        {
            try
            {
                if (!enabled)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
                    continue;
                }
                using var key = Registry.CurrentUser.CreateSubKey(path, writable: true);
                if (key is null) continue;
                key.SetValue(null, AppInfo.IsBeta ? MenuText + " (Beta)" : MenuText);
                key.SetValue("Icon", $"\"{exe}\",0");
                using var cmd = key.CreateSubKey("command", writable: true);
                // %V is the folder for both the folder item and the background verb.
                cmd?.SetValue(null, $"\"{exe}\" \"%V\"");
            }
            catch (Exception ex)
            {
                AppLog.Warn("ShellIntegration", $"Registry update failed for {path}: {ex.Message}");
            }
        }

        try
        {
            if (!enabled)
            {
                if (File.Exists(ShimPath)) File.Delete(ShimPath);
                return;
            }
            var dir = Path.GetDirectoryName(ShimPath);
            if (dir is null || !Directory.Exists(dir)) return; // WindowsApps missing — skip the shim
            var shim =
                "@echo off\r\n" +
                ":: Written by Drover (Settings > General > Shell integration).\r\n" +
                $"if \"%~1\"==\"\" (start \"\" \"{exe}\" \"%CD%\") else (start \"\" \"{exe}\" \"%~f1\")\r\n";
            if (!File.Exists(ShimPath) || File.ReadAllText(ShimPath) != shim)
                AtomicFile.WriteAllText(ShimPath, shim);
        }
        catch (Exception ex)
        {
            AppLog.Warn("ShellIntegration", $"drover.cmd update failed: {ex.Message}");
        }
    }
}
