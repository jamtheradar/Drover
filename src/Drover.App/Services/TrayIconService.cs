using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Drawing;
using System.Windows.Threading;
using Drover.App.ViewModels;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ContextMenuStrip = System.Windows.Forms.ContextMenuStrip;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using MouseEventArgs = System.Windows.Forms.MouseEventArgs;
using MouseButtons = System.Windows.Forms.MouseButtons;

namespace Drover.App.Services;

public enum TrayState { Idle, Working, Attention, Inactive }

/// <summary>
/// Owns the system tray icon and aggregates per-tab attention state into a
/// single tray-state enum. Icons are loaded once and cached; the NotifyIcon
/// reference is swapped on state change rather than reconstructed (GDI handle
/// hygiene — see ICONS.md).
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly Icon _idle;
    private readonly Icon _working;
    private readonly Icon _attention;
    private readonly Icon _inactive;

    // Tabs that have transitioned Working→Idle while not selected — i.e. a
    // turn finished while the user wasn't looking. Cleared when the user
    // selects that tab or it goes back to Working.
    private readonly HashSet<TerminalTabViewModel> _needsAttention = new();

    private ShellViewModel? _shell;
    private DispatcherTimer? _debounce;
    private TrayState _current = TrayState.Inactive;
    private bool _disposed;

    public TrayIconService()
    {
        _idle      = LoadIcon("drover-idle.ico");
        _working   = LoadIcon("drover-working.ico");
        _attention = LoadIcon("drover-attention.ico");
        _inactive  = LoadIcon("drover-inactive.ico");

        var menu = new ContextMenuStrip();
        var showItem = new ToolStripMenuItem("Show Drover");
        showItem.Click += (_, _) => ShowMainWindow();
        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => System.Windows.Application.Current?.Shutdown();
        menu.Items.Add(showItem);
        menu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = _inactive,
            Text = "Drover",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.MouseClick += OnTrayMouseClick;
        _notifyIcon.DoubleClick += (_, _) => ShowMainWindow();
    }

    /// <summary>
    /// Bind the tray to a <see cref="ShellViewModel"/>. Subscribes to the tab
    /// collection and per-tab attention transitions, then computes initial state.
    /// </summary>
    public void Bind(ShellViewModel shell)
    {
        if (_shell is not null) return;
        _shell = shell;

        shell.Tabs.CollectionChanged += OnTabsChanged;
        foreach (var t in shell.Tabs) Subscribe(t);
        shell.PropertyChanged += OnShellPropertyChanged;

        Recompute();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_shell is null) return;
        if (e.PropertyName == nameof(ShellViewModel.SelectedTab) && _shell.SelectedTab is { } sel)
        {
            if (_needsAttention.Remove(sel)) ScheduleRecompute();
        }
    }

    private void OnTabsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (TerminalTabViewModel t in e.OldItems) Unsubscribe(t);
        if (e.NewItems is not null)
            foreach (TerminalTabViewModel t in e.NewItems) Subscribe(t);
        ScheduleRecompute();
    }

    private void Subscribe(TerminalTabViewModel t)
    {
        t.AttentionChanged += OnTabAttentionChanged;
    }

    private void Unsubscribe(TerminalTabViewModel t)
    {
        t.AttentionChanged -= OnTabAttentionChanged;
        _needsAttention.Remove(t);
    }

    private void OnTabAttentionChanged(object? sender, (AttentionState previous, AttentionState next) e)
    {
        if (sender is not TerminalTabViewModel tab) return;

        // Working→Idle on an unfocused tab = "Claude finished, user hasn't looked yet".
        if (e.previous == AttentionState.Working
            && e.next == AttentionState.Idle
            && !ReferenceEquals(_shell?.SelectedTab, tab))
        {
            _needsAttention.Add(tab);
        }
        if (e.next == AttentionState.Working)
            _needsAttention.Remove(tab);

        ScheduleRecompute();
    }

    /// <summary>
    /// 200ms debounce — Claude's spinner can blip Working↔Idle during tool
    /// calls and we don't want the tray icon to flash.
    /// </summary>
    private void ScheduleRecompute()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        if (!app.Dispatcher.CheckAccess())
        {
            app.Dispatcher.BeginInvoke(new Action(ScheduleRecompute));
            return;
        }
        _debounce ??= new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _debounce.Tick -= OnDebounceTick;
        _debounce.Tick += OnDebounceTick;
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDebounceTick(object? sender, EventArgs e)
    {
        _debounce?.Stop();
        Recompute();
    }

    private void Recompute()
    {
        var next = ComputeState();
        if (next == _current) return;
        _current = next;
        _notifyIcon.Icon = next switch
        {
            TrayState.Working   => _working,
            TrayState.Attention => _attention,
            TrayState.Inactive  => _inactive,
            _                   => _idle,
        };
        _notifyIcon.Text = next switch
        {
            TrayState.Working   => "Drover — working",
            TrayState.Attention => "Drover — needs attention",
            TrayState.Inactive  => "Drover — no sessions",
            _                   => "Drover — idle",
        };
    }

    private TrayState ComputeState()
    {
        if (_shell is null || _shell.Tabs.Count == 0) return TrayState.Inactive;
        if (_needsAttention.Count > 0) return TrayState.Attention;
        foreach (var t in _shell.Tabs)
            if (t.Attention == AttentionState.Working) return TrayState.Working;
        return TrayState.Idle;
    }

    private void OnTrayMouseClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) ShowMainWindow();
    }

    private static void ShowMainWindow()
    {
        var app = System.Windows.Application.Current;
        var win = app?.MainWindow;
        if (win is null) return;
        if (win.WindowState == System.Windows.WindowState.Minimized)
            win.WindowState = System.Windows.WindowState.Normal;
        win.Show();
        win.Activate();
        win.Topmost = true;
        win.Topmost = false;
        win.Focus();
    }

    private static Icon LoadIcon(string name)
    {
        var uri = new Uri($"pack://application:,,,/Resources/Icons/{name}", UriKind.Absolute);
        var streamInfo = System.Windows.Application.GetResourceStream(uri)
            ?? throw new InvalidOperationException($"Icon resource missing: {name}");
        using var stream = streamInfo.Stream;
        return new Icon(stream);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (_shell is not null)
        {
            _shell.Tabs.CollectionChanged -= OnTabsChanged;
            _shell.PropertyChanged -= OnShellPropertyChanged;
            foreach (var t in _shell.Tabs) Unsubscribe(t);
            _shell = null;
        }

        _debounce?.Stop();
        _debounce = null;

        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _idle.Dispose();
        _working.Dispose();
        _attention.Dispose();
        _inactive.Dispose();
    }
}
