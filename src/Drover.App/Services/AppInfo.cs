using System.Reflection;

namespace Drover.App.Services;

public static class AppInfo
{
    public static string Version { get; } = ResolveVersion();
    public static string DisplayVersion => $"v{Version}";

    private static string ResolveVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus >= 0 ? info[..plus] : info;
        }
        return asm.GetName().Version?.ToString(3) ?? "0.0.0";
    }
}
