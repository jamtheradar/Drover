using System;
using Microsoft.Win32;

namespace Drover.App.Services;

/// <summary>
/// Reads/writes the per-user startup entry under
/// HKCU\Software\Microsoft\Windows\CurrentVersion\Run. Per-user is the right
/// scope here — installation is per-user MSIX/zip extract, no admin needed,
/// and toggles in Settings shouldn't prompt for elevation. Failures are
/// swallowed so a locked-down policy machine doesn't break the Save flow.
/// </summary>
public static class WindowsStartupService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Drover";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string s && !string.IsNullOrWhiteSpace(s);
        }
        catch { return false; }
    }

    public static void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return;
            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe)) return;
                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
            }
            else
            {
                if (key.GetValue(ValueName) is not null)
                    key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch
        {
            // Group policy / antivirus can lock this key; nothing useful to do.
        }
    }
}
