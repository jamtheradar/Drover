using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Drover.App.Services;

/// <summary>
/// Registers a system-wide hotkey that brings the target window to the foreground.
/// Default: Ctrl+Shift+Backtick.
/// </summary>
public sealed class GlobalHotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint VK_OEM_3 = 0xC0; // backtick on US layouts

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly int _id = 0xC0C1;
    private readonly Window _window;
    private HwndSource? _source;
    private bool _registered;

    public event EventHandler? Pressed;

    public GlobalHotkey(Window window)
    {
        _window = window;
    }

    public bool Register()
    {
        var helper = new WindowInteropHelper(_window);
        if (helper.Handle == IntPtr.Zero)
        {
            _window.SourceInitialized += (_, _) => TryRegister();
            return true;
        }
        return TryRegister();
    }

    private bool TryRegister()
    {
        var helper = new WindowInteropHelper(_window);
        var hwnd = helper.Handle;
        if (hwnd == IntPtr.Zero) return false;

        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(WndProc);

        _registered = RegisterHotKey(hwnd, _id, MOD_CONTROL | MOD_SHIFT, VK_OEM_3);
        return _registered;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == _id)
        {
            Pressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_registered)
        {
            var hwnd = new WindowInteropHelper(_window).Handle;
            if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, _id);
            _registered = false;
        }
        _source?.RemoveHook(WndProc);
        _source = null;
    }
}
