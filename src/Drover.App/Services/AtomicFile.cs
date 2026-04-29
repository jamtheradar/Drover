using System.IO;

namespace Drover.App.Services;

internal static class AtomicFile
{
    public static void WriteAllText(string path, string contents)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, contents);
        File.Move(tmp, path, overwrite: true);
    }
}
