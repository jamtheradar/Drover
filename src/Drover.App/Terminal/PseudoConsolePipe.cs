using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Drover.App.Terminal;

/// <summary>
/// One half of the pseudoconsole's two-pipe duplex. We allocate two
/// instances: input (renderer → child stdin) and output (child stdout → renderer).
/// </summary>
internal sealed class PseudoConsolePipe : IDisposable
{
    public SafeFileHandle ReadSide { get; }
    public SafeFileHandle WriteSide { get; }

    public PseudoConsolePipe()
    {
        if (!NativeMethods.CreatePipe(out var read, out var write, IntPtr.Zero, 0))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreatePipe failed");
        ReadSide = read;
        WriteSide = write;
    }

    public void Dispose()
    {
        ReadSide.Dispose();
        WriteSide.Dispose();
    }
}
