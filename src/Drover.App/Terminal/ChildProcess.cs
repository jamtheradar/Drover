using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Drover.App.Terminal;

/// <summary>
/// Wraps the STARTUPINFOEX + PROCESS_INFORMATION pair returned from a
/// CreateProcess call that has the pseudoconsole attached as a thread
/// attribute. The pseudoconsole supplies stdin/stdout/stderr to the child
/// internally, so the child sees a console as if it were running in conhost.
/// </summary>
internal sealed class ChildProcess : IDisposable
{
    private NativeMethods.STARTUPINFOEX _startup;
    private NativeMethods.PROCESS_INFORMATION _info;
    private bool _disposed;

    public int Pid => (int)_info.dwProcessId;

    public bool HasExited
    {
        get
        {
            try { return Process.GetProcessById(Pid).HasExited; }
            catch { return true; }
        }
    }

    public static ChildProcess Start(string command, PseudoConsole console,
        string? workingDirectory = null,
        IReadOnlyDictionary<string, string>? environmentOverrides = null)
    {
        var startup = ConfigureStartupInfo(console.Handle);
        var envBlock = BuildEnvironmentBlock(environmentOverrides);
        try
        {
            var info = LaunchProcess(ref startup, command, workingDirectory, envBlock);
            return new ChildProcess { _startup = startup, _info = info };
        }
        finally
        {
            if (envBlock != IntPtr.Zero) Marshal.FreeHGlobal(envBlock);
        }
    }

    private static NativeMethods.STARTUPINFOEX ConfigureStartupInfo(IntPtr hPC)
    {
        // First call: query the size needed for a 1-attribute list.
        IntPtr lpSize = IntPtr.Zero;
        var ok = NativeMethods.InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref lpSize);
        if (ok || lpSize == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList (size query) failed");

        var startup = new NativeMethods.STARTUPINFOEX();
        startup.StartupInfo.cb = (uint)Marshal.SizeOf<NativeMethods.STARTUPINFOEX>();
        startup.lpAttributeList = Marshal.AllocHGlobal((int)lpSize);

        if (!NativeMethods.InitializeProcThreadAttributeList(startup.lpAttributeList, 1, 0, ref lpSize))
        {
            Marshal.FreeHGlobal(startup.lpAttributeList);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "InitializeProcThreadAttributeList failed");
        }

        if (!NativeMethods.UpdateProcThreadAttribute(
            startup.lpAttributeList,
            0,
            NativeMethods.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE,
            hPC,
            (IntPtr)IntPtr.Size,
            IntPtr.Zero,
            IntPtr.Zero))
        {
            NativeMethods.DeleteProcThreadAttributeList(startup.lpAttributeList);
            Marshal.FreeHGlobal(startup.lpAttributeList);
            throw new Win32Exception(Marshal.GetLastWin32Error(), "UpdateProcThreadAttribute failed");
        }

        return startup;
    }

    private static NativeMethods.PROCESS_INFORMATION LaunchProcess(
        ref NativeMethods.STARTUPINFOEX startup,
        string commandLine,
        string? workingDirectory,
        IntPtr environmentBlock)
    {
        // CreateProcess can write into lpCommandLine; hand it a writable copy
        // (the trailing NUL keeps the marshaler honest about length).
        var cmd = (commandLine + '\0').ToCharArray();
        var flags = NativeMethods.EXTENDED_STARTUPINFO_PRESENT;
        if (environmentBlock != IntPtr.Zero) flags |= NativeMethods.CREATE_UNICODE_ENVIRONMENT;
        if (!NativeMethods.CreateProcess(
                null,
                cmd,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                flags,
                environmentBlock,
                workingDirectory,
                ref startup,
                out var pi))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed");
        }
        return pi;
    }

    /// <summary>
    /// Builds a Unicode environment block (sorted KEY=VALUE pairs separated by NULs, terminated
    /// with a double-NUL) by overlaying <paramref name="overrides"/> onto the parent process's
    /// current environment. Returns IntPtr.Zero when no overrides — callers should then pass
    /// IntPtr.Zero to CreateProcess so the child plain-inherits.
    ///
    /// Caller owns the returned handle and must <see cref="Marshal.FreeHGlobal"/> it.
    /// </summary>
    private static IntPtr BuildEnvironmentBlock(IReadOnlyDictionary<string, string>? overrides)
    {
        if (overrides is null || overrides.Count == 0) return IntPtr.Zero;
        // CreateProcess requires the env block to be sorted (case-insensitive Unicode order).
        var merged = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry kv in Environment.GetEnvironmentVariables())
            merged[(string)kv.Key] = (string?)kv.Value ?? string.Empty;
        foreach (var kv in overrides)
            merged[kv.Key] = kv.Value ?? string.Empty;
        var sb = new StringBuilder();
        foreach (var kv in merged)
        {
            sb.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
        }
        sb.Append('\0');
        return Marshal.StringToHGlobalUni(sb.ToString());
    }

    public void Kill()
    {
        if (_info.hProcess == IntPtr.Zero) return;
        try { NativeMethods.TerminateProcess(_info.hProcess, 1); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_startup.lpAttributeList != IntPtr.Zero)
        {
            try { NativeMethods.DeleteProcThreadAttributeList(_startup.lpAttributeList); } catch { }
            try { Marshal.FreeHGlobal(_startup.lpAttributeList); } catch { }
            _startup.lpAttributeList = IntPtr.Zero;
        }
        if (_info.hProcess != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_info.hProcess);
            _info.hProcess = IntPtr.Zero;
        }
        if (_info.hThread != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_info.hThread);
            _info.hThread = IntPtr.Zero;
        }
    }
}
