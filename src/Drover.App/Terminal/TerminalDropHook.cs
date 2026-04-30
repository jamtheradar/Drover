using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Drover.App.Terminal;

/// <summary>
/// Adds Win32-level drag-and-drop file support to the terminal renderer
/// HWND. WPF's drag-drop pipeline can't see native HWND children (the
/// airspace problem), so AllowDrop on the WPF parent does nothing for the
/// terminal. We register the HWND with the shell via DragAcceptFiles and
/// subclass it for WM_DROPFILES; dropped paths flow back to the PTY as
/// space-separated quoted strings — Claude Code's TUI ingests them on the
/// prompt line.
/// </summary>
internal sealed class TerminalDropHook : IDisposable
{
    private const int WM_DROPFILES = 0x0233;
    private const uint SUBCLASS_ID = 0xD0DD;

    private readonly List<IntPtr> _hwnds = new();
    private readonly Action<string[]> _onDrop;
    private readonly NativeMethods.SubclassProc _proc;
    private bool _disposed;

    private TerminalDropHook(Action<string[]> onDrop)
    {
        _onDrop = onDrop;
        _proc = SubclassProc;
    }

    /// <summary>
    /// Walks <paramref name="root"/> to find the Microsoft.Terminal.Wpf
    /// HwndHost ("TerminalContainer"), grabs its HWND, installs the hook.
    /// Returns null if the host isn't realized yet — caller should retry on
    /// the next dispatcher frame.
    /// </summary>
    public static TerminalDropHook? TryInstall(DependencyObject root, Action<string[]> onDrop)
    {
        var host = FindHwndHost(root);
        if (host is null) return null;
        var hostHwnd = host.Handle;
        if (hostHwnd == IntPtr.Zero) return null;

        var hook = new TerminalDropHook(onDrop);
        if (!hook.Install(hostHwnd)) return null;
        return hook;
    }

    private bool Install(IntPtr hostHwnd)
    {
        // The renderer that actually owns keyboard focus is a child of the
        // HwndHost's window — OS-level WM_KEYDOWN goes there, not to the
        // host. Subclass the host AND every descendant so we catch keys
        // regardless of which HWND ends up with focus. DragAcceptFiles is
        // also registered on every level so a drop anywhere in the
        // terminal tree fires WM_DROPFILES.
        var targets = new List<IntPtr> { hostHwnd };
        NativeMethods.EnumChildWindows(hostHwnd, (h, _) => { targets.Add(h); return true; }, IntPtr.Zero);

        bool any = false;
        foreach (var h in targets)
        {
            if (NativeMethods.SetWindowSubclass(h, _proc, SUBCLASS_ID, IntPtr.Zero))
            {
                _hwnds.Add(h);
                NativeMethods.DragAcceptFiles(h, true);
                any = true;
            }
        }
        return any;
    }

    private IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint subclassId, IntPtr refData)
    {
        if (msg == WM_DROPFILES)
        {
            try
            {
                var paths = QueryDroppedFiles(wParam);
                if (paths.Length > 0)
                {
                    try { _onDrop(paths); }
                    catch { /* host gone — swallow */ }
                }
            }
            finally
            {
                NativeMethods.DragFinish(wParam);
            }
            return IntPtr.Zero;
        }

        return NativeMethods.DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private static string[] QueryDroppedFiles(IntPtr hDrop)
    {
        // First call with index 0xFFFFFFFF returns the count.
        uint count = NativeMethods.DragQueryFile(hDrop, 0xFFFFFFFF, null, 0);
        if (count == 0) return Array.Empty<string>();

        var result = new string[count];
        var buf = new StringBuilder(260);
        for (uint i = 0; i < count; i++)
        {
            // Probe for required size, then read.
            uint len = NativeMethods.DragQueryFile(hDrop, i, null, 0);
            if (buf.Capacity < len + 1) buf.Capacity = (int)len + 1;
            buf.Length = 0;
            NativeMethods.DragQueryFile(hDrop, i, buf, (uint)buf.Capacity);
            result[i] = buf.ToString();
        }
        return result;
    }

    private static HwndHost? FindHwndHost(DependencyObject? d)
    {
        if (d is null) return null;
        if (d is HwndHost h) return h;
        int n = VisualTreeHelper.GetChildrenCount(d);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            var found = FindHwndHost(child);
            if (found is not null) return found;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var h in _hwnds)
        {
            try { NativeMethods.DragAcceptFiles(h, false); } catch { }
            try { NativeMethods.RemoveWindowSubclass(h, _proc, SUBCLASS_ID); } catch { }
        }
        _hwnds.Clear();
    }

    private static class NativeMethods
    {
        public delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint subclassId, IntPtr refData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc proc, uint id, IntPtr refData);

        [DllImport("comctl32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool RemoveWindowSubclass(IntPtr hWnd, SubclassProc proc, uint id);

        [DllImport("comctl32.dll")]
        public static extern IntPtr DefSubclassProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        public static extern uint DragQueryFile(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

        [DllImport("shell32.dll")]
        public static extern void DragFinish(IntPtr hDrop);

        [DllImport("shell32.dll")]
        public static extern void DragAcceptFiles(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAccept);

        public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool EnumChildWindows(IntPtr hwndParent, EnumWindowsProc proc, IntPtr lParam);
    }
}
