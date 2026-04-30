using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using Drover.App.Services;
using Drover.App.ViewModels;

namespace Drover.App.Views;

public partial class ShellWindow : Window
{
    private GlobalHotkey? _globalHotkey;
    private SettingsStore? _settings;
    private ToastNotifier? _toast;

    public ShellWindow()
    {
        InitializeComponent();
        WelcomeVersionText.Text = AppInfo.DisplayVersion;
        _globalHotkey = new GlobalHotkey(this);
        _globalHotkey.Pressed += (_, _) => BringSelfForward();
        // Force Windows to paint the OS title bar dark instead of using the user's
        // accent colour (blue by default). Has to run after the HWND is created.
        SourceInitialized += (_, _) => TryApplyImmersiveDarkTitleBar();
        Loaded += (_, _) =>
        {
            if (!_globalHotkey.Register() && DataContext is ShellViewModel vm)
                vm.StatusText = "global hotkey Ctrl+Shift+` registration failed (already taken?)";
        };
        Closed += (_, _) => _globalHotkey?.Dispose();
        DataContextChanged += OnDataContextChanged;
        ApplyShortcutBindings();

        for (int i = 1; i <= 9; i++)
        {
            var index = i - 1;
            InputBindings.Add(new KeyBinding(
                new RelayCommand(() => SelectTabAt(index)),
                new KeyGesture((Key)((int)Key.D1 + index), ModifierKeys.Control)));
        }

        InputBindings.Add(new KeyBinding(
            new RelayCommand(ShowCheatSheet),
            new KeyGesture(Key.F1)));

        SourceInitialized += (_, _) => ApplyWindowPlacement();
        Closing += OnWindowClosing;
        Activated += (_, _) => RefreshTitle();
        Deactivated += (_, _) => RefreshTitle();

        // Escape closes the dashboard/tokenomics/memory overlays when one is open.
        // PreviewKeyDown fires before the terminal HwndHost can swallow it; when no
        // overlay is open we leave the event unhandled so terminal Escape still works.
        PreviewKeyDown += OnWindowPreviewKeyDown;

        // Alt-modified shortcuts (Alt+E, Alt+P, ...) arrive as WM_SYSKEYDOWN. The
        // hosted ConPTY terminal is an HwndHost — native HWNDs sit outside WPF's
        // input pipeline and consume those messages before InputBindings run.
        // Hooking WM_SYSKEYDOWN at the HwndSource level lets us match configured
        // gestures before the terminal eats them.
        SourceInitialized += (_, _) => InstallSysKeyHook();
    }

    private System.Windows.Interop.HwndSource? _hwndSource;
    private void InstallSysKeyHook()
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        _hwndSource = System.Windows.Interop.HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WindowProc);
    }

    private System.IntPtr WindowProc(System.IntPtr hwnd, int msg, System.IntPtr wParam, System.IntPtr lParam, ref bool handled)
    {
        const int WM_SYSKEYDOWN = 0x0104;
        const int WM_KEYDOWN = 0x0100;
        if (msg != WM_SYSKEYDOWN && msg != WM_KEYDOWN) return System.IntPtr.Zero;

        var virtualKey = (int)wParam;
        var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(virtualKey);
        if (key == Key.None) return System.IntPtr.Zero;

        var mods = System.Windows.Input.Keyboard.Modifiers;
        // GetKeyState catches Alt even when WPF's Keyboard.Modifiers hasn't been
        // updated (it's behind during raw HWND messages). Bit 0x8000 = currently down.
        if ((NativeGetKeyState(0x12) & 0x8000) != 0) mods |= ModifierKeys.Alt;   // VK_MENU
        if ((NativeGetKeyState(0x11) & 0x8000) != 0) mods |= ModifierKeys.Control; // VK_CONTROL
        if ((NativeGetKeyState(0x10) & 0x8000) != 0) mods |= ModifierKeys.Shift; // VK_SHIFT

        // We only care about combos with at least one modifier — bare letters are
        // typed into the terminal.
        if (mods == ModifierKeys.None) return System.IntPtr.Zero;

        foreach (var b in _shortcutBindings)
        {
            if (b.Gesture is KeyGesture g && g.Key == key && g.Modifiers == mods)
            {
                if (b.Command?.CanExecute(null) == true)
                {
                    b.Command.Execute(null);
                    handled = true;
                    return new System.IntPtr(1);
                }
            }
        }
        return System.IntPtr.Zero;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetKeyState")]
    private static extern short NativeGetKeyState(int nVirtKey);

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Escape closes any open overlay.
        if (e.Key == Key.Escape)
        {
            if (DataContext is not ShellViewModel vm) return;
            if (!vm.DashboardActive && !vm.TokenomicsActive && !vm.MemoryActive) return;
            vm.DashboardActive = false;
            vm.TokenomicsActive = false;
            vm.MemoryActive = false;
            e.Handled = true;
            return;
        }

        // Tab / Shift+Tab / arrow keys are intercepted by WPF's focus-traversal
        // and directional-navigation logic (HwndHost.TranslateAccelerator) before
        // they reach the terminal HWND, so Claude Code's Shift+Tab (cycle modes)
        // and history-navigation arrows silently move WPF focus instead. Catch
        // them at PreviewKeyDown — same pattern proven to work for Escape — and
        // forward the corresponding VT byte sequence to the focused tab's PTY.
        // Modified Tab (Ctrl/Alt) is left alone so app shortcuts keep working.
        if (Keyboard.Modifiers != ModifierKeys.None &&
            Keyboard.Modifiers != ModifierKeys.Shift)
        {
            return;
        }

        var seq = e.Key switch
        {
            Key.Tab => (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift ? "\x1b[Z" : "\t",
            Key.Up => "\x1b[A",
            Key.Down => "\x1b[B",
            Key.Right => "\x1b[C",
            Key.Left => "\x1b[D",
            _ => null
        };
        if (seq is null) return;

        var tab = ResolveFocusedTab();
        if (tab is null) return;

        if (tab.SendInput(seq, appendReturn: false))
            e.Handled = true;
    }

    /// <summary>
    /// Walks up from the keyboard-focused element to find the
    /// <see cref="TerminalTabView"/> (each terminal pane's wrapper) and
    /// returns the bound <see cref="TerminalTabViewModel"/>. Falls back to
    /// the shell's SelectedTab if no terminal is currently in the focus path
    /// (e.g. focus is on a filter box).
    /// </summary>
    private TerminalTabViewModel? ResolveFocusedTab()
    {
        if (Keyboard.FocusedElement is DependencyObject d)
        {
            for (var cur = d; cur is not null; cur = System.Windows.Media.VisualTreeHelper.GetParent(cur))
            {
                if (cur is TerminalTabView tv && tv.DataContext is TerminalTabViewModel vm)
                    return vm;
            }
        }
        return (DataContext as ShellViewModel)?.SelectedTab;
    }

    private readonly System.Collections.Generic.List<KeyBinding> _shortcutBindings = new();

    /// <summary>
    /// Tells DWM to use the immersive dark title bar for this window. Without this the
    /// OS paints the title bar in the user's Windows accent colour (blue by default),
    /// which clashes with the dark app chrome. Attribute 20 is the public name on
    /// Windows 10 20H1+; attribute 19 was the pre-release alias on early 1903 builds —
    /// we try 20 first and fall back. Failure is silent: pre-1903 just gets the
    /// stock title bar, which is the same situation as today.
    /// </summary>
    private void TryApplyImmersiveDarkTitleBar()
    {
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == System.IntPtr.Zero) return;
            int useDark = 1;
            if (DwmSetWindowAttribute(hwnd, 20, ref useDark, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref useDark, sizeof(int));

            // DWMWA_CAPTION_COLOR (35) — explicit dark caption colour. With dark-mode
            // alone, focused windows still pick up the user's Windows accent (blue by
            // default). Setting an explicit colour overrides that and matches the
            // Drover chrome (#1e1e1e) regardless of focus state. Win11 22000+.
            // COLORREF byte order is 0x00BBGGRR. For #1E1E1E it's 0x001E1E1E.
            int captionColor = unchecked((int)0x001E1E1E);
            DwmSetWindowAttribute(hwnd, 35, ref captionColor, sizeof(int));

            // Force the non-client area (title bar) to repaint. Without this, applying
            // the attribute before the first paint sometimes leaves the active-state
            // accent colour visible until the next OS-driven redraw.
            const uint SWP_NOMOVE = 0x0002;
            const uint SWP_NOSIZE = 0x0001;
            const uint SWP_NOZORDER = 0x0004;
            const uint SWP_NOACTIVATE = 0x0010;
            const uint SWP_FRAMECHANGED = 0x0020;
            SetWindowPos(hwnd, System.IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        catch { /* dwmapi unavailable — older OS, give up silently */ }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(System.IntPtr hwnd, int attr, ref int value, int size);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetWindowPos(System.IntPtr hWnd, System.IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private void ApplyShortcutBindings()
    {
        foreach (var b in _shortcutBindings) InputBindings.Remove(b);
        _shortcutBindings.Clear();

        _settings ??= (DataContext as ShellViewModel)?.Settings;
        var sc = _settings?.Current.Shortcuts ?? new KeyboardShortcuts();
        Bind(sc.CommandPalette, ShowCommandPalette);
        Bind(sc.CycleTabForward, () => CycleTab(+1));
        Bind(sc.CycleTabBackward, () => CycleTab(-1));
        Bind(sc.RenameTab, RenameCurrentTab);
        Bind(sc.FindInTab, FindInCurrentTab);
        Bind(sc.GlobalFind, OpenGlobalFind);
        Bind(sc.Settings, ShowSettings);
        Bind(sc.DetachProbe, RunDetachProbe);
        Bind(sc.TogglePlanPanel, TogglePlanPanelHalfWidth);
        Bind(sc.ToggleTaskPanel, ToggleTaskPanelHalfWidth);
        Bind(sc.ToggleFileExplorerPanel, ToggleFileExplorerPanelDefaultWidth);
    }

    /// <summary>
    /// Toggles the plan panel. When opening, sets the right-pane width to 50% of
    /// the window so the plan gets a generous default. The user can still drag the
    /// splitter; the new width is then remembered for future toggles in the session.
    /// </summary>
    private void TogglePlanPanelHalfWidth()
    {
        if (DataContext is not ShellViewModel vm) return;
        var willOpen = !vm.PlanPanelVisible;
        vm.PlanPanelVisible = willOpen;
        if (!willOpen) return;

        var halfWidth = ActualWidth * 0.5;
        if (halfWidth < 240) halfWidth = 240;
        if (halfWidth > 1400) halfWidth = 1400;
        vm.RightPaneWidth = halfWidth;
    }

    private void ToggleTaskPanelHalfWidth()
    {
        if (DataContext is not ShellViewModel vm) return;
        var willOpen = !vm.TaskPanelVisible;
        vm.TaskPanelVisible = willOpen;
        if (!willOpen) return;

        var halfWidth = ActualWidth * 0.5;
        if (halfWidth < 240) halfWidth = 240;
        if (halfWidth > 1400) halfWidth = 1400;
        vm.RightPaneWidth = halfWidth;
    }

    /// <summary>
    /// Toggles the file explorer panel. Opens at ~35% of window width — narrower than
    /// Plan/Task because file trees don't need as much horizontal room.
    /// </summary>
    private void ToggleFileExplorerPanelDefaultWidth()
    {
        if (DataContext is not ShellViewModel vm) return;
        var willOpen = !vm.FileExplorerPanelVisible;
        vm.FileExplorerPanelVisible = willOpen;
        if (!willOpen) return;

        var width = ActualWidth * 0.35;
        if (width < 240) width = 240;
        if (width > 1000) width = 1000;
        vm.RightPaneWidth = width;
    }

    private void Bind(string text, System.Action action)
    {
        var gesture = GestureParser.TryParse(text);
        if (gesture is null) return;
        var b = new KeyBinding(new RelayCommand(action), gesture);
        InputBindings.Add(b);
        _shortcutBindings.Add(b);
    }

    private void RefreshTitle()
    {
        if (DataContext is not ShellViewModel vm) { Title = "Drover"; return; }
        if (IsActive) { Title = "Drover"; return; }
        var waiting = 0;
        foreach (var t in vm.Tabs)
            if (t.Attention == AttentionState.Idle) waiting++;
        Title = waiting > 0 ? $"Drover · {waiting} waiting" : "Drover";
    }

    private void ApplyWindowPlacement()
    {
        _settings ??= (DataContext as ShellViewModel)?.Settings;
        var p = _settings?.Current.Window;
        if (p is null) return;
        var screenW = SystemParameters.VirtualScreenWidth;
        var screenH = SystemParameters.VirtualScreenHeight;
        if (p.Left + 40 < SystemParameters.VirtualScreenLeft + screenW &&
            p.Top + 40 < SystemParameters.VirtualScreenTop + screenH &&
            p.Width >= 400 && p.Height >= 300)
        {
            Left = p.Left;
            Top = p.Top;
            Width = p.Width;
            Height = p.Height;
        }
        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (DataContext is ShellViewModel vm)
        {
            var busy = 0;
            foreach (var t in vm.Tabs)
                if (t.Attention == AttentionState.Working) busy++;

            if (busy > 0)
            {
                var msg = busy == 1
                    ? "1 session is still working. Closing now will terminate it. Quit anyway?"
                    : $"{busy} sessions are still working. Closing now will terminate them. Quit anyway?";
                var result = MessageBox.Show(this, msg, "Drover",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (result != MessageBoxResult.Yes)
                {
                    e.Cancel = true;
                    return;
                }
            }
        }
        SaveWindowPlacement();
    }

    private void SaveWindowPlacement()
    {
        _settings ??= (DataContext as ShellViewModel)?.Settings;
        if (_settings is null) return;
        var maximized = WindowState == WindowState.Maximized;
        var bounds = maximized ? RestoreBounds : new Rect(Left, Top, Width, Height);
        _settings.Update(_settings.Current with
        {
            Window = new WindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height, maximized)
        });
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => ShowSettings();

    private void ShowSettings()
    {
        _settings ??= (DataContext as ShellViewModel)?.Settings;
        if (_settings is null) return;
        var dlg = new SettingsWindow(_settings) { Owner = this };
        dlg.ShowDialog();
    }

    private void CheatSheet_Click(object sender, RoutedEventArgs e) => ShowCheatSheet();

    private void ShowCheatSheet()
    {
        var win = new CheatSheetWindow { Owner = this };
        win.Show();
    }

    private void About_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AboutDialog { Owner = this };
        dlg.ShowDialog();
    }

    public void ShowWhatsNewIfNeeded()
    {
        _settings ??= (DataContext as ShellViewModel)?.Settings;
        if (_settings is null) return;
        if (_settings.Current.LastSeenVersion == AppInfo.Version) return;

        var dlg = new WhatsNewWindow { Owner = this };
        dlg.ShowDialog();
        _settings.Update(_settings.Current with { LastSeenVersion = AppInfo.Version });
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is ShellViewModel oldVm)
        {
            oldVm.Tabs.CollectionChanged -= OnTabsChanged;
            oldVm.PropertyChanged -= OnShellPropertyChanged;
        }
        if (e.NewValue is ShellViewModel newVm)
        {
            newVm.Tabs.CollectionChanged += OnTabsChanged;
            newVm.PropertyChanged += OnShellPropertyChanged;
            foreach (var t in newVm.Tabs) Subscribe(t);
            UpdateSecondaryColumn(newVm);

            _settings ??= newVm.Settings;
            if (_settings is not null)
            {
                _settings.Changed -= OnSettingsChanged;
                _settings.Changed += OnSettingsChanged;
            }
            ApplyShortcutBindings();

            _toast = (App.Current as App)?.Services.GetService(typeof(ToastNotifier)) as ToastNotifier;
            if (_toast is not null)
            {
                _toast.TabClicked += OnToastTabClicked;
            }
        }
    }

    private void OnSettingsChanged(object? sender, System.EventArgs e) => ApplyShortcutBindings();

    private void OnShellPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SecondaryTab) && sender is ShellViewModel vm)
            UpdateSecondaryColumn(vm);
    }

    private void UpdateSecondaryColumn(ShellViewModel vm)
    {
        if (SecondaryColumn is null) return;
        SecondaryColumn.Width = vm.SecondaryTab is null
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        SecondaryColumn.MinWidth = vm.SecondaryTab is null ? 0 : 200;
    }

    private void TabHeader_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (sender is System.Windows.Controls.Panel panel && panel.Tag is TerminalTabViewModel tab)
        {
            if (DataContext is ShellViewModel vm) vm.CloseTabCommand.Execute(tab);
            e.Handled = true;
        }
    }

    private void OnToastTabClicked(object? sender, int tabIndex)
    {
        Dispatcher.Invoke(() =>
        {
            if (DataContext is not ShellViewModel vm) return;
            if (tabIndex >= 0 && tabIndex < vm.Tabs.Count) vm.SelectedTab = vm.Tabs[tabIndex];
            BringSelfForward();
        });
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (TerminalTabViewModel t in e.OldItems) Unsubscribe(t);
        if (e.NewItems is not null)
            foreach (TerminalTabViewModel t in e.NewItems) Subscribe(t);
    }

    private void Subscribe(TerminalTabViewModel tab) => tab.AttentionChanged += OnTabAttention;
    private void Unsubscribe(TerminalTabViewModel tab) => tab.AttentionChanged -= OnTabAttention;

    private void OnTabAttention(object? sender, (AttentionState previous, AttentionState next) e)
    {
        if (DataContext is not ShellViewModel vm) return;
        var tab = sender as TerminalTabViewModel;
        if (tab is not null) vm.Activity.Record(tab, e.previous, e.next);

        RefreshTitle();

        if (e is not { previous: AttentionState.Working, next: AttentionState.Idle }) return;

        var isForegroundTab = IsActive &&
            (ReferenceEquals(vm.SelectedTab, tab) || ReferenceEquals(vm.SecondaryTab, tab));
        if (isForegroundTab) return;

        TaskbarFlash.Flash(this);

        if (tab is not null && _toast is not null)
        {
            var idx = vm.Tabs.IndexOf(tab);
            if (idx >= 0) _toast.NotifyIdle(tab, idx);
        }
    }

    private void BringSelfForward()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Topmost = true;
        Activate();
        Topmost = false;
        Focus();
    }

    private void FindInCurrentTab()
    {
        if (DataContext is not ShellViewModel vm || vm.SelectedTab is null) return;
        OpenFindFor(vm.SelectedTab);
    }

    internal void OpenFindFor(ViewModels.TerminalTabViewModel tab)
    {
        var win = new FindWindow(tab) { Owner = this };
        win.Show();
    }

    private void RenameCurrentTab()
    {
        if (DataContext is not ShellViewModel vm || vm.SelectedTab is null) return;
        var dlg = new RenameDialog(vm.SelectedTab.Title) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.NewName is not null)
            vm.SelectedTab.Title = dlg.NewName;
    }

    private void SelectTabAt(int index)
    {
        if (DataContext is not ShellViewModel vm) return;
        if (index < 0 || index >= vm.Tabs.Count) return;
        vm.SelectedTab = vm.Tabs[index];
    }

    private void OpenGlobalFind()
    {
        var win = new GlobalFindWindow { Owner = this };
        win.Show();
    }

    private void CycleTab(int delta)
    {
        if (DataContext is not ShellViewModel vm || vm.Tabs.Count == 0) return;
        var idx = vm.SelectedTab is null ? 0 : vm.Tabs.IndexOf(vm.SelectedTab);
        idx = (idx + delta + vm.Tabs.Count) % vm.Tabs.Count;
        vm.SelectedTab = vm.Tabs[idx];
    }

    private void RunDetachProbe()
    {
        if (DataContext is not ShellViewModel vm) return;
        vm.StatusText = DetachReattachProbe.Run(this, vm);
    }

    private void ShowCommandPalette()
    {
        if (DataContext is not ShellViewModel vm) return;
        var palette = new CommandPalette(this, vm) { Owner = this };
        palette.Show();
    }

    private void PopOut_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is ViewModels.TerminalTabViewModel tab)
            PopOutTab(tab);
    }

    private void SplitRight_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is ViewModels.TerminalTabViewModel tab)
            vm.SplitRightCommand.Execute(tab);
    }

    private void OpenLog_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        if (mi.Tag is not ViewModels.TerminalTabViewModel tab) return;
        if (tab.LogFilePath is null || !System.IO.File.Exists(tab.LogFilePath)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(tab.LogFilePath) { UseShellExecute = true });
        }
        catch { /* shell verb not registered — ignore */ }
    }

    private void Find_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        if (mi.Tag is ViewModels.TerminalTabViewModel tab) OpenFindFor(tab);
    }

    private void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        if (mi.Tag is ViewModels.TerminalTabViewModel tab) ExportHistoryFor(tab);
    }

    /// <summary>
    /// Runs the markdown export for a tab and surfaces the result via the
    /// status bar + a clickable toast (clicking the toast reveals the file in
    /// Explorer). Shared between the right-click menu and the command palette.
    /// </summary>
    internal void ExportHistoryFor(ViewModels.TerminalTabViewModel tab)
    {
        if (DataContext is not ShellViewModel vm) return;
        var path = Services.HistoryExporter.Export(tab);
        if (path is null)
        {
            vm.StatusText = $"export failed: {tab.Title} (no log available)";
            return;
        }
        vm.StatusText = $"exported: {path}";
        _toast?.NotifyExportSaved(tab.Title, path);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is ViewModels.TerminalTabViewModel tab)
            vm.OpenTabFolder(tab);
    }

    private void InstallHook_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        if (sender is System.Windows.Controls.MenuItem mi && mi.Tag is ViewModels.TerminalTabViewModel tab)
            vm.InstallHookForTab(tab);
    }

    private void OpenHooksLog_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is ShellViewModel vm) vm.OpenHooksLog();
    }

    private void AddProject_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        var dlg = new AddProjectDialog { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is not null)
            vm.AddProject(dlg.Result);
    }

    private void RemoveProject_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        if (mi.Tag is not Models.ProjectDefinition project) return;
        vm.RemoveProjectCommand.Execute(project);
    }

    private void LaunchDangerously_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        if (mi.Tag is not Models.ProjectDefinition project) return;
        vm.OpenProjectDangerously(project);
    }

    private void EditProject_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ShellViewModel vm) return;
        if (sender is not System.Windows.Controls.MenuItem mi) return;
        if (mi.Tag is not Models.ProjectDefinition project) return;
        var dlg = new AddProjectDialog(project) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.Result is not null)
            vm.ReplaceProject(project, dlg.Result);
    }

    internal void PopOutTab(ViewModels.TerminalTabViewModel tab)
    {
        if (DataContext is not ShellViewModel vm) return;

        var view = FindTerminalView(tab);
        if (view is null)
        {
            vm.StatusText = $"pop out: could not locate view for {tab.Title}";
            return;
        }
        var terminal = view.Terminal;
        if (terminal.Parent is not System.Windows.Controls.Panel originPanel)
        {
            vm.StatusText = "pop out: terminal parent is not a Panel";
            return;
        }

        originPanel.Children.Remove(terminal);
        var popped = new PoppedOutWindow(tab, terminal, originPanel) { Owner = this };
        popped.Show();
        vm.StatusText = $"popped out: {tab.Title}";
    }

    private TerminalTabView? FindTerminalView(ViewModels.TerminalTabViewModel tab)
    {
        foreach (var v in Descendants(this))
        {
            if (v is TerminalTabView tv && ReferenceEquals(tv.DataContext, tab)) return tv;
        }
        return null;
    }

    private static System.Collections.Generic.IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var grand in Descendants(child)) yield return grand;
        }
    }
}

internal sealed class RelayCommand : ICommand
{
    private readonly System.Action _exec;
    public RelayCommand(System.Action exec) => _exec = exec;
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => _exec();
#pragma warning disable CS0067 // ICommand requires the event; CanExecute is always true so it's never raised.
    public event System.EventHandler? CanExecuteChanged;
#pragma warning restore CS0067
}
