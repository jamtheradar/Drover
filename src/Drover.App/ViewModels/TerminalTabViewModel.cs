using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Drover.App.Models;
using Drover.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Drover.App.Terminal;
using MsTerm = Microsoft.Terminal.Wpf;

namespace Drover.App.ViewModels;

public sealed partial class TerminalTabViewModel : ObservableObject, IDisposable
{
    [ObservableProperty] private string _title;
    [ObservableProperty] private AttentionState _attention = AttentionState.Unknown;
    [ObservableProperty] private string _workingElapsedText = string.Empty;
    [ObservableProperty] private bool _attentionStale;
    [ObservableProperty] private string _modelText = string.Empty;
    [ObservableProperty] private string _effortText = string.Empty;
    [ObservableProperty] private string _contextPercentText = string.Empty;
    [ObservableProperty] private string _costText = string.Empty;
    [ObservableProperty] private long _contextTokens;
    [ObservableProperty] private double _sessionCost;
    [ObservableProperty] private bool _hooksInstalled;

    // Git status (populated by GitStatusService polling, raised through ShellViewModel).
    // Empty when the project root isn't a git repo or git isn't on PATH.
    [ObservableProperty] private string _gitText = string.Empty;
    [ObservableProperty] private bool _gitDirty;
    [ObservableProperty] private bool _gitHasRepo;

    // Permission-prompt strip text. Populated from a Notification hook event's
    // `message` field; cleared when any other hook arrives (the user has moved
    // on — UserPromptSubmit / Stop / a tool call). XAML binds the strip's
    // visibility to the non-empty/empty state of this string.
    [ObservableProperty] private string _permissionPromptText = string.Empty;

    // One-line live activity strip — "Reading file…", "Running bash…" — derived
    // from PreToolUse / PostToolUse hooks. Cleared on Stop / SessionStart, or
    // after ~3s of silence (decay timer) so a long-running response between
    // tool calls doesn't leave stale text on the header.
    [ObservableProperty] private string _currentToolText = string.Empty;

    // CC-pushed status fields (from the configured statusLine command).
    // These are authoritative when set — CC owns the numbers; we just display.
    // Untouched when CC hasn't pushed (e.g. < v2.1.97 or statusLine not installed).
    [ObservableProperty] private double _ccContextPercent;
    [ObservableProperty] private string _ccOutputStyle = string.Empty;
    [ObservableProperty] private string _ccVimMode = string.Empty;
    [ObservableProperty] private double _ccRateLimit5hPercent;
    [ObservableProperty] private string _ccRateLimit5hResetText = string.Empty;
    // Raw resets_at epoch (UTC) behind the display text — lets the shell compare
    // windows across tabs instead of trusting whichever tab pushed last.
    [ObservableProperty] private DateTime? _ccRateLimit5hResetUtc;
    [ObservableProperty] private double _ccRateLimit7dPercent;
    [ObservableProperty] private string _ccRateLimit7dResetText = string.Empty;
    [ObservableProperty] private long _ccLinesAdded;
    [ObservableProperty] private long _ccLinesRemoved;
    [ObservableProperty] private double _ccCostUsd;
    [ObservableProperty] private string _ccWorktreeBranch = string.Empty;
    // Raw permission_mode value as pushed by CC: "default" | "plan" | "acceptEdits" | "bypassPermissions".
    // Empty when the field hasn't been pushed (older CC, statusLine not wired). PermissionModeText
    // is the friendly display form; the XAML binds to that and uses the raw value to colour the badge.
    [ObservableProperty] private string _ccPermissionMode = string.Empty;
    [ObservableProperty] private string _permissionModeText = string.Empty;
    [ObservableProperty] private DateTime _ccLastUpdateUtc;

    // Codex pill state. Codex has no statusLine event, so these fields are
    // driven directly by hook envelope contents on each event:
    //  - _codexModel mirrors evt.Model (latest assistant turn's model id).
    //  - _codexToolCountThisTurn increments on every Codex PreToolUse and
    //    resets to 0 on UserPromptSubmit. Kept visible after Stop so the
    //    user can see "the last turn used 7 tools" between prompts.
    // CodexToolCountText is a one-shot string format ("3 tools" / "1 tool")
    // so the XAML can bind without a multi-binding converter.
    [ObservableProperty] private string? _codexModel;
    [ObservableProperty] private int _codexToolCountThisTurn;
    [ObservableProperty] private string _codexToolCountText = string.Empty;

    // Display-formatted derived strings — populated alongside the raw Cc* fields
    // from the same statusLine push, so the XAML can bind without value converters.
    [ObservableProperty] private string _rateLimit5hText = string.Empty;
    [ObservableProperty] private string _rateLimit7dText = string.Empty;
    [ObservableProperty] private string _linesText = string.Empty;
    [ObservableProperty] private string _statusUpdatedText = string.Empty;
    [ObservableProperty] private string _contextPercentSubText = string.Empty;

    private DateTime? _workingSince;
    private DispatcherTimer? _workingTimer;
    private DispatcherTimer? _toolDecayTimer;
    // Codex-only: fires once 10s after the tab launches if no hook event has
    // arrived yet. The user has almost certainly forgotten to run `codex trust .`
    // for the project (project-scope hooks no-op silently otherwise). Cleared
    // on the first hook event so a healthy install never raises a toast.
    private DispatcherTimer? _codexHookWatchdog;
    private bool _codexHookWatchdogFired;

    // True once a statusLine push has supplied an authoritative effort.level (or a
    // thinking.enabled=false). Polled transcript scanning won't overwrite EffortText
    // after this — CC's push is the source of truth.
    private bool _effortFromStatusLine;

    private AttentionMonitor? _monitor;
    private ClipboardIntegration? _clipboard;
    private SessionLogger? _logger;
    private TranscriptCapture? _transcript;
    private DroverTerminal? _control;

    public TerminalTabViewModel(
        ProjectDefinition project,
        string? title = null,
        string? fontFamily = null,
        int? fontSize = null,
        bool resume = false,
        string? hooksUrl = null,
        bool dangerouslySkipPermissions = false,
        string? claudeExecutablePath = null,
        string? defaultClaudeModel = null,
        string? codexExecutablePath = null,
        string? geminiExecutablePath = null,
        System.Collections.Generic.IReadOnlyDictionary<string, string>? globalEnvVars = null,
        Drover.App.Services.SessionLogOptions? logOptions = null,
        bool disableClaudeMouseClicks = true)
    {
        Project = project;
        _title = title ?? project.Name;
        FontFamily = fontFamily ?? "Cascadia Code";
        FontSize = fontSize ?? 12;
        Resume = resume;
        HooksUrl = hooksUrl;
        DangerouslySkipPermissions = dangerouslySkipPermissions;
        ClaudeExecutablePath = claudeExecutablePath;
        DefaultClaudeModel = defaultClaudeModel;
        CodexExecutablePath = codexExecutablePath;
        GeminiExecutablePath = geminiExecutablePath;
        GlobalEnvVars = globalEnvVars;
        LogOptions = logOptions;
        DisableClaudeMouseClicks = disableClaudeMouseClicks;
        SessionId = Guid.NewGuid().ToString("N");
    }

    public ProjectDefinition Project { get; }
    public string FontFamily { get; }
    public int FontSize { get; }
    public bool Resume { get; }
    public string? HooksUrl { get; }
    public bool DangerouslySkipPermissions { get; }
    public string? ClaudeExecutablePath { get; }
    public string? DefaultClaudeModel { get; }
    public string? CodexExecutablePath { get; }
    public string? GeminiExecutablePath { get; }
    public System.Collections.Generic.IReadOnlyDictionary<string, string>? GlobalEnvVars { get; }
    public Drover.App.Services.SessionLogOptions? LogOptions { get; }
    public bool DisableClaudeMouseClicks { get; }
    public string SessionId { get; }

    /// <summary>
    /// Frozen <see cref="SolidColorBrush"/> derived from <see cref="ProjectDefinition.TabColor"/>,
    /// or null when no per-project colour is set. The tab header style binds to this and falls
    /// back to the chrome default when null. For Codex projects with no explicit colour we
    /// substitute the <c>Tab.Default.Codex</c> palette brush so a fresh Codex tab is visually
    /// distinct from a Claude tab; Claude / ClaudeDirect / Pwsh still return null (current
    /// behaviour) per the spec — only Codex gets a kind-default.
    /// </summary>
    public Brush? TabColorBrush
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(Project.TabColor))
            {
                try
                {
                    var color = (Color)ColorConverter.ConvertFromString(Project.TabColor)!;
                    var b = new SolidColorBrush(color);
                    b.Freeze();
                    return b;
                }
                catch { /* fall through to per-kind default */ }
            }
            if (Project.Kind == ProjectKind.Codex)
            {
                try
                {
                    var app = System.Windows.Application.Current;
                    if (app?.TryFindResource("Tab.Default.Codex") is SolidColorBrush b) return b;
                }
                catch { }
            }
            if (Project.Kind == ProjectKind.Gemini)
            {
                try
                {
                    var app = System.Windows.Application.Current;
                    if (app?.TryFindResource("Tab.Default.Gemini") is SolidColorBrush b) return b;
                }
                catch { }
            }
            return null;
        }
    }

    /// <summary>
    /// Friendly per-tab label for the agent powering the session ("Claude",
    /// "Claude (direct)", "Codex", "PowerShell"). Single source of truth used
    /// by the session-log preamble, the tab-badge tooltip, and any agent-aware
    /// copy. Adding a new <see cref="ProjectKind"/>? Extend this switch and the
    /// <see cref="AgentBadge"/> mapping in lockstep.
    /// </summary>
    public string AgentDisplayName => Project.Kind switch
    {
        ProjectKind.Claude        => "Claude",
        ProjectKind.ClaudeDirect  => "Claude (direct)",
        ProjectKind.Codex         => "Codex",
        ProjectKind.Gemini        => "Gemini",
        ProjectKind.Pwsh          => "PowerShell",
        _                         => "Agent",
    };

    /// <summary>
    /// Two-letter affordance shown in the tab header: <c>CC</c> Claude,
    /// <c>CD</c> Claude (direct), <c>CX</c> Codex, <c>GC</c> Gemini, <c>PS</c> pwsh. Text rather
    /// than icon by design — keeps the option open for future agents without
    /// the icon-asset pipeline.
    /// </summary>
    public string AgentBadge => Project.Kind switch
    {
        ProjectKind.Claude        => "CC",
        ProjectKind.ClaudeDirect  => "CD",
        ProjectKind.Codex         => "CX",
        ProjectKind.Gemini        => "GC",
        ProjectKind.Pwsh          => "PS",
        _                         => "??",
    };

    // A non-null Theme is required for DroverTerminal to honor FontFamily/FontSize; if
    // left null it skips SetTheme entirely and falls back to the renderer's defaults.
    public MsTerm.TerminalTheme Theme { get; } = new()
    {
        DefaultBackground = 0x0C0C0C,
        DefaultForeground = 0xCCCCCC,
        DefaultSelectionBackground = 0x3D3D3D,
        CursorStyle = MsTerm.CursorStyle.BlinkingBar,
        ColorTable = new uint[]
        {
            0x0C0C0C, 0x1F0FC5, 0x0EA113, 0x009CC1, 0xDA3700, 0x981788, 0xDD963A, 0xCCCCCC,
            0x767676, 0x5648E7, 0x0CC616, 0xA5F1F9, 0xFF783B, 0x9E00B4, 0xD6D661, 0xF2F2F2
        }
    };

    public string? LogFilePath => _logger?.Path;

    /// <summary>
    /// Path to the clean Markdown transcript (.transcript.md). Populated in both the
    /// default Transcript level and Full; null only when session logging is Off. Unlike
    /// <see cref="LogFilePath"/> this exists in the default config, so it's the primary
    /// source for <see cref="HistoryExporter"/>.
    /// </summary>
    public string? TranscriptFilePath => _transcript?.Path;

    /// <summary>
    /// Forces the background session-log writer to drain pending chunks and
    /// flush the StreamWriter. Used by <see cref="HistoryExporter"/> so the
    /// exported markdown reflects the current on-screen state, not whatever
    /// happened to be flushed up to ~250 ms ago. Flushes both the raw VT trace
    /// and the clean transcript.
    /// </summary>
    public void FlushSessionLog()
    {
        _logger?.Flush();
        _transcript?.Flush();
    }

    /// <summary>True for Claude-flavored kinds (Claude, ClaudeDirect) — controls whether
    /// CLAUDE_CODE_NO_FLICKER, --model/--resume/--dangerously-skip-permissions, and the
    /// implicit "claude" command apply.</summary>
    private bool IsClaudeKind => Project.Kind is ProjectKind.Claude or ProjectKind.ClaudeDirect;

    /// <summary>True for Codex tabs. Drives the simplified status-pill template branch
    /// (model + activity dot + per-turn tool count; no context %, cost, rate limits or
    /// thinking) and gates the Codex hook event branches in <see cref="OnHookEvent"/>.</summary>
    public bool IsCodexTab => Project.Kind == ProjectKind.Codex;

    /// <summary>True for Gemini tabs. Drives the same simplified status-pill branch as
    /// Codex (model + activity dot + per-turn tool count; no context %, cost, rate limits or
    /// thinking).</summary>
    public bool IsGeminiTab => Project.Kind == ProjectKind.Gemini;

    public string StartupCommandLine
    {
        get
        {
            // Raw override — user owns shell, cwd, env. Used to experiment with cmd.exe /
            // git-bash / nu / direct claude.exe etc. for input-latency testing.
            if (!string.IsNullOrWhiteSpace(Project.LaunchCommand))
                return Project.LaunchCommand!;

            // ClaudeDirect: skip the pwsh wrapper entirely. Working directory and env vars
            // ride on CreateProcess's lpCurrentDirectory/lpEnvironment (see StartupWorkingDirectory
            // and StartupEnvironment below).
            if (Project.Kind == ProjectKind.ClaudeDirect)
                return BuildDirectClaudeCommand();

            var sb = new StringBuilder();
            if (Project.Kind == ProjectKind.Claude)
            {
                sb.Append("$env:CLAUDE_CODE_NO_FLICKER=1; ");
                if (DisableClaudeMouseClicks)
                    sb.Append("$env:CLAUDE_CODE_DISABLE_MOUSE_CLICKS=1; ");
            }

            // Hooks-gateway plumbing: configured Claude hooks can POST to this URL
            // tagged with the session id. Harmless if no hooks are configured.
            sb.Append($"$env:DROVER_SESSION_ID='{SessionId}'; ");
            if (!string.IsNullOrEmpty(HooksUrl))
                sb.Append($"$env:DROVER_HOOKS_URL='{EscapeSingleQuote(HooksUrl!)}'; ");

            // Global env first, then per-project overrides — project values win.
            if (GlobalEnvVars is { Count: > 0 })
            {
                foreach (var kv in GlobalEnvVars)
                    sb.Append($"$env:{kv.Key}='{EscapeSingleQuote(kv.Value)}'; ");
            }
            if (Project.EnvVars is { Count: > 0 })
            {
                foreach (var kv in Project.EnvVars)
                    sb.Append($"$env:{kv.Key}='{EscapeSingleQuote(kv.Value)}'; ");
            }

            sb.Append($"cd '{EscapeSingleQuote(Project.Path)}'");

            var command = Project.Command;
            if (string.IsNullOrWhiteSpace(command) && Project.Kind == ProjectKind.Claude)
            {
                command = !string.IsNullOrWhiteSpace(ClaudeExecutablePath)
                    ? $"& '{EscapeSingleQuote(ClaudeExecutablePath!)}'"
                    : "claude";
            }
            else if (string.IsNullOrWhiteSpace(command) && Project.Kind == ProjectKind.Codex)
            {
                command = !string.IsNullOrWhiteSpace(CodexExecutablePath)
                    ? $"& '{EscapeSingleQuote(CodexExecutablePath!)}'"
                    : "codex";
            }
            else if (string.IsNullOrWhiteSpace(command) && Project.Kind == ProjectKind.Gemini)
            {
                command = !string.IsNullOrWhiteSpace(GeminiExecutablePath)
                    ? $"& '{EscapeSingleQuote(GeminiExecutablePath!)}'"
                    : "gemini";
            }

            if (!string.IsNullOrWhiteSpace(command))
            {
                sb.Append("; ").Append(command);
                if (!string.IsNullOrWhiteSpace(Project.Args))
                    sb.Append(' ').Append(Project.Args);
                if (Project.Kind == ProjectKind.Claude
                    && string.IsNullOrWhiteSpace(Project.Command)
                    && !ContainsModelFlag(Project.Args))
                {
                    var model = !string.IsNullOrWhiteSpace(Project.Model)
                        ? Project.Model
                        : DefaultClaudeModel;
                    if (!string.IsNullOrWhiteSpace(model))
                        sb.Append(" --model ").Append(model);
                }
                else if ((Project.Kind == ProjectKind.Codex || Project.Kind == ProjectKind.Gemini)
                    && string.IsNullOrWhiteSpace(Project.Command)
                    && !ContainsModelFlag(Project.Args)
                    && !string.IsNullOrWhiteSpace(Project.Model))
                {
                    sb.Append(" --model ").Append(Project.Model);
                }
                if (Resume && Project.Kind == ProjectKind.Claude)
                    sb.Append(" --resume");
                if (DangerouslySkipPermissions && Project.Kind == ProjectKind.Claude)
                    sb.Append(" --dangerously-skip-permissions");
            }

            return $"pwsh.exe -NoLogo -NoExit -Command \"{sb}\"";
        }
    }

    /// <summary>Working directory passed to CreateProcess for ClaudeDirect tabs. Null for shell-
    /// wrapped kinds (the wrapper handles cd).</summary>
    public string? StartupWorkingDirectory =>
        Project.Kind == ProjectKind.ClaudeDirect && string.IsNullOrWhiteSpace(Project.LaunchCommand)
            ? Project.Path
            : null;

    /// <summary>Environment overrides for ClaudeDirect tabs — the same DROVER_SESSION_ID /
    /// DROVER_HOOKS_URL / global+per-project vars the pwsh wrapper would set, but layered onto
    /// the parent env via CreateProcess's lpEnvironment instead.</summary>
    public System.Collections.Generic.IReadOnlyDictionary<string, string>? StartupEnvironment
    {
        get
        {
            if (Project.Kind != ProjectKind.ClaudeDirect) return null;
            if (!string.IsNullOrWhiteSpace(Project.LaunchCommand)) return null;
            var env = new System.Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["CLAUDE_CODE_NO_FLICKER"] = "1",
                ["DROVER_SESSION_ID"] = SessionId,
            };
            if (DisableClaudeMouseClicks) env["CLAUDE_CODE_DISABLE_MOUSE_CLICKS"] = "1";
            if (!string.IsNullOrEmpty(HooksUrl)) env["DROVER_HOOKS_URL"] = HooksUrl!;
            if (GlobalEnvVars is { Count: > 0 })
                foreach (var kv in GlobalEnvVars) env[kv.Key] = kv.Value;
            if (Project.EnvVars is { Count: > 0 })
                foreach (var kv in Project.EnvVars) env[kv.Key] = kv.Value;
            return env;
        }
    }

    /// <summary>Builds the bare command line for ClaudeDirect mode — the claude executable plus
    /// args/--model/--resume/--dangerously-skip-permissions. No shell, no cd, no env setup;
    /// those land via CreateProcess parameters.</summary>
    private string BuildDirectClaudeCommand()
    {
        var sb = new StringBuilder();
        var command = Project.Command;
        if (string.IsNullOrWhiteSpace(command))
        {
            command = !string.IsNullOrWhiteSpace(ClaudeExecutablePath)
                ? $"\"{ClaudeExecutablePath}\""
                : "claude";
        }
        sb.Append(command);
        if (!string.IsNullOrWhiteSpace(Project.Args))
            sb.Append(' ').Append(Project.Args);
        if (string.IsNullOrWhiteSpace(Project.Command) && !ContainsModelFlag(Project.Args))
        {
            var model = !string.IsNullOrWhiteSpace(Project.Model)
                ? Project.Model
                : DefaultClaudeModel;
            if (!string.IsNullOrWhiteSpace(model))
                sb.Append(" --model ").Append(model);
        }
        if (Resume) sb.Append(" --resume");
        if (DangerouslySkipPermissions) sb.Append(" --dangerously-skip-permissions");
        return sb.ToString();
    }

    private static string EscapeSingleQuote(string s) => s.Replace("'", "''");

    private static bool ContainsModelFlag(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return false;
        return args.Contains("--model", StringComparison.OrdinalIgnoreCase)
            || System.Text.RegularExpressions.Regex.IsMatch(args, @"(^|\s)-m(\s|=|$)");
    }

    public async Task AttachAsync(DroverTerminal control)
    {
        for (int i = 0; i < 40 && control.Connection is null; i++)
            await Task.Delay(100);
        if (control.Connection is null) return;
        _control = control;

        // Snapshot the model known at attach time: per-project override wins over
        // the global Claude default. For Codex the hook-pushed model lands later
        // via OnHookEvent and won't update the already-written preamble — the
        // log header is a snapshot, not a live header.
        var modelAtLaunch = !string.IsNullOrWhiteSpace(Project.Model)
            ? Project.Model
            : (Project.Kind == ProjectKind.Codex || Project.Kind == ProjectKind.Gemini ? null : DefaultClaudeModel);
        _logger = new SessionLogger(control, Title, LogOptions, AgentDisplayName, modelAtLaunch);
        _logger.Attach();

        // The clean transcript runs in parallel with the raw VT trace. It's driven by
        // hook events (see OnHookEvent) and reads the agent's own JSONL — no ANSI scraping.
        if (_logger.TranscriptPath is { } tp)
        {
            _transcript = new TranscriptCapture(tp, AgentDisplayName, modelAtLaunch, Project.Name);
            _transcript.Attach();
        }

        // OSC-title scraping is a fallback for projects without Drover hooks. When
        // hooks are installed the gateway will deliver authoritative attention
        // signals, so skip the regex-per-output-chunk work entirely — no Attach,
        // no temporary CPU spike during CC's chatty startup banner.
        _monitor = new AttentionMonitor(control);
        _monitor.StateChanged += (_, s) => SetAttention(s);
        if (!HooksInstalled) _monitor.Attach();

        // If we never see a recognisable title within 5s, surface that in the UI
        // so a missing/broken Claude launch doesn't sit silently as a grey dot.
        _ = Task.Delay(TimeSpan.FromSeconds(5)).ContinueWith(_ =>
        {
            var app = System.Windows.Application.Current;
            if (app is null) return;
            app.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (Attention == AttentionState.Unknown) AttentionStale = true;
            }));
        });

        _clipboard = new ClipboardIntegration(control);
        _clipboard.Attach();

        if (Project.Kind == ProjectKind.Codex)
        {
            StartCodexHookWatchdog();
            WriteMissingApiKeyBannerIfNeeded();
        }
    }

    /// <summary>
    /// Codex requires <c>OPENAI_API_KEY</c> (env var) or a populated
    /// <c>~/.codex/auth.json</c> (from <c>codex login</c>). If neither is
    /// present, Codex emits a cryptic error and dies. Write an informational
    /// banner into the renderer before Codex starts so the user has a clear
    /// diagnosis. Don't block launch — Codex may evolve other auth flows
    /// (OAuth, device code) that Drover wouldn't know about.
    /// </summary>
    private void WriteMissingApiKeyBannerIfNeeded()
    {
        if (HasOpenAiApiKey()) return;
        var pty = _control?.Connection;
        if (pty is null) return;
        const string msg =
            "\r\n\x1b[33m[Drover] Codex needs OPENAI_API_KEY. Set it in this project's environment variables " +
            "(right-click ▸ Edit project ▸ API key) or run `codex login` to populate ~/.codex/auth.json.\x1b[0m\r\n";
        try { pty.RaiseRendererOutput(msg); } catch { /* renderer torn down */ }
    }

    private bool HasOpenAiApiKey()
    {
        if (Project.EnvVars is { } pv && pv.TryGetValue("OPENAI_API_KEY", out var pk) && !string.IsNullOrEmpty(pk))
            return true;
        if (GlobalEnvVars is { } gv && gv.TryGetValue("OPENAI_API_KEY", out var gk) && !string.IsNullOrEmpty(gk))
            return true;
        var inherited = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (!string.IsNullOrEmpty(inherited)) return true;
        try
        {
            var home = Environment.GetEnvironmentVariable("USERPROFILE")
                       ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home)
                && System.IO.File.Exists(System.IO.Path.Combine(home, ".codex", "auth.json")))
                return true;
        }
        catch { }
        return false;
    }

    /// <summary>
    /// Starts the one-shot 10-second "no hook events yet" watchdog for Codex
    /// tabs. Cancels itself the moment <see cref="OnHookEvent"/> fires; if it
    /// reaches Tick the user almost certainly needs to <c>codex trust .</c>
    /// the project directory. DispatcherTimer is dispatcher-affine so the
    /// arming has to happen on the UI thread.
    /// </summary>
    private void StartCodexHookWatchdog()
    {
        DispatchToUi(() =>
        {
            if (_codexHookWatchdogFired) return;
            _codexHookWatchdog?.Stop();
            _codexHookWatchdog = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(10),
            };
            _codexHookWatchdog.Tick += OnCodexHookWatchdogTick;
            _codexHookWatchdog.Start();
        });
    }

    private void OnCodexHookWatchdogTick(object? sender, EventArgs e)
    {
        _codexHookWatchdog?.Stop();
        if (_codexHookWatchdogFired) return;
        _codexHookWatchdogFired = true;
        const string msg = "Codex isn't firing hooks into Drover. If you installed project-local hooks, try `codex trust .` in the tab and reopen.";
        CodexHookSilent?.Invoke(this, msg);
    }

    private void CancelCodexHookWatchdog()
    {
        if (_codexHookWatchdog is null) return;
        DispatchToUi(() =>
        {
            _codexHookWatchdog?.Stop();
            _codexHookWatchdog = null;
        });
        // Mark fired=true so a late re-arm (e.g. statusLine after Tick already
        // queued) can't replay the toast.
        _codexHookWatchdogFired = true;
    }

    public event EventHandler<(AttentionState previous, AttentionState next)>? AttentionChanged;

    /// <summary>
    /// Fires when a Claude Code <c>Notification</c> hook arrives — typically a
    /// permission prompt awaiting user approval. The string is the hook's
    /// <c>message</c> field (e.g. "Claude needs your permission to use Edit").
    /// Used by the shell to raise a toast / taskbar flash distinct from the
    /// generic working→idle transition.
    /// </summary>
    public event EventHandler<string>? PermissionPrompted;

    /// <summary>
    /// Codex-only: raised once if no hook event arrives within 10 seconds of
    /// the tab being attached. Codex's project-scope hooks no-op silently
    /// without <c>codex trust .</c>; this is the user-visible nudge. Carries
    /// the toast text the shell should display.
    /// </summary>
    public event EventHandler<string>? CodexHookSilent;

    /// <summary>
    /// Fires after a Claude Code statusLine push has been parsed and the Cc* fields
    /// updated. Lets the shell aggregate global state (e.g. account-wide rate limits)
    /// across tabs without polling. Always raised on the UI dispatcher.
    /// </summary>
    public event EventHandler? StatusLineUpdated;

    /// <summary>Raised by the tab's "hook off" badge; the shell installs hooks for this project.</summary>
    public event EventHandler? InstallHooksRequested;

    [RelayCommand]
    private void RequestInstallHooks()
    {
        if (!HooksInstalled) InstallHooksRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Fires when the set of running subagents changes (Task/Agent tool launch or
    /// return, or a turn boundary clearing everything). May be raised from the
    /// gateway's background thread — consumers marshal to the UI dispatcher.
    /// </summary>
    public event EventHandler? RunningAgentsChanged;

    // Running subagents keyed by "agent:{agent_id}" or "tool:{tool_use_id}" →
    // subagent_type. SubagentStart/SubagentStop is the primary signal — with
    // background subagents (CC 2.x default) the Task/Agent tool call returns
    // immediately while the agent keeps running, so the PreToolUse/PostToolUse
    // pair is kept only as a fallback for older CC versions without
    // SubagentStart. Stop clears tool-keyed entries only (a background agent
    // outlives its parent turn); SessionStart/SessionEnd clear everything.
    // Guarded by the lock because the gateway writes from a background task
    // while the Agents panel reads from the UI thread.
    private readonly System.Collections.Generic.Dictionary<string, string> _runningSubagents = new();
    private readonly object _runningSubagentsLock = new();

    /// <summary>
    /// Distinct subagent types currently running in this tab (e.g. "Explore",
    /// "code-reviewer"). Snapshot — safe to enumerate from any thread.
    /// </summary>
    public System.Collections.Generic.IReadOnlyList<string> RunningAgentTypes
    {
        get
        {
            lock (_runningSubagentsLock)
            {
                var set = new System.Collections.Generic.HashSet<string>(_runningSubagents.Values, StringComparer.OrdinalIgnoreCase);
                var list = new System.Collections.Generic.List<string>(set);
                list.Sort(StringComparer.OrdinalIgnoreCase);
                return list;
            }
        }
    }

    private static bool IsSubagentLaunchTool(string? tool)
        => tool is "Task" or "Agent";

    /// <summary>
    /// Pulls <c>subagent_type</c> out of a Task/Agent tool_input blob. The field
    /// is optional on the wire — omitted means the default agent, which Claude
    /// Code documents as "general-purpose".
    /// </summary>
    private static string ReadSubagentType(string? toolInputJson)
    {
        if (!string.IsNullOrWhiteSpace(toolInputJson))
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(toolInputJson);
                if (doc.RootElement.TryGetProperty("subagent_type", out var v)
                    && v.ValueKind == System.Text.Json.JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(v.GetString()))
                    return v.GetString()!;
            }
            catch { /* malformed tool_input — fall through to default */ }
        }
        return "general-purpose";
    }

    private void TrackSubagents(HookEvent evt)
    {
        bool changed = false;
        if (evt.Type is "SubagentStart" or "SubagentStop")
        {
            var agentType = string.IsNullOrWhiteSpace(evt.AgentType) ? "general-purpose" : evt.AgentType!;
            var key = $"agent:{evt.AgentId ?? agentType}";
            lock (_runningSubagentsLock)
            {
                changed = evt.Type == "SubagentStart"
                    ? _runningSubagents.TryAdd(key, agentType)
                    // A Stop whose id doesn't match a Start (schema drift
                    // between CC versions) still means one agent finished —
                    // drop one agent-keyed entry rather than leak a pulse.
                    // Only when the Stop names an agent type, though: CC also
                    // fires internal SubagentStops (~5s after every turn's
                    // Stop, empty agent_type, ids that never had a Start) for
                    // its own sidechains, and treating those as "one agent
                    // finished" evicted real running agents from the panel.
                    : _runningSubagents.Remove(key)
                      || (!string.IsNullOrWhiteSpace(evt.AgentType) && RemoveOneAgentEntry(evt.AgentType));
            }
        }
        else if (IsSubagentLaunchTool(evt.Tool) && evt.Type is "PreToolUse" or "PostToolUse")
        {
            var agentType = ReadSubagentType(evt.ToolInput);
            // tool_use_id pairs launch/return exactly; without it fall back to the
            // type name — parallel same-type agents then collapse to one entry,
            // which is fine for a "who's working" pulse.
            var key = $"tool:{evt.ToolUseId ?? agentType}";
            lock (_runningSubagentsLock)
            {
                changed = evt.Type == "PreToolUse"
                    ? _runningSubagents.TryAdd(key, agentType)
                    : _runningSubagents.Remove(key);
            }
        }
        else if (evt.Type is "Stop" or "SessionStart" or "SessionEnd")
        {
            // Turn end only closes the tool-pair fallback entries — background
            // subagents keep running past Stop and are closed by SubagentStop.
            // Session boundaries clear everything.
            var clearAll = evt.Type is "SessionStart" or "SessionEnd";
            lock (_runningSubagentsLock)
            {
                if (clearAll)
                {
                    changed = _runningSubagents.Count > 0;
                    _runningSubagents.Clear();
                }
                else
                {
                    foreach (var key in System.Linq.Enumerable.ToArray(_runningSubagents.Keys))
                    {
                        if (key.StartsWith("tool:", StringComparison.Ordinal))
                            changed |= _runningSubagents.Remove(key);
                    }
                }
            }
        }
        if (changed) RunningAgentsChanged?.Invoke(this, EventArgs.Empty);
    }

    // Caller holds _runningSubagentsLock. Removes the first agent-keyed entry,
    // preferring one whose type matches; returns true if something was removed.
    private bool RemoveOneAgentEntry(string? agentType)
    {
        string? fallback = null;
        foreach (var kv in _runningSubagents)
        {
            if (!kv.Key.StartsWith("agent:", StringComparison.Ordinal)) continue;
            if (!string.IsNullOrEmpty(agentType)
                && string.Equals(kv.Value, agentType, StringComparison.OrdinalIgnoreCase))
                return _runningSubagents.Remove(kv.Key);
            fallback ??= kv.Key;
        }
        return fallback is not null && _runningSubagents.Remove(fallback);
    }

    /// <summary>
    /// Receives hook events routed by HooksGateway for this tab's session id.
    /// Maps Claude Code lifecycle events to AttentionState. Hooks race with
    /// the OSC-title monitor; both feed <see cref="SetAttention"/> and last-write
    /// wins. The hook signal is more reliable for Stop/Notification (no spinner
    /// animation lag) and the OSC signal is the fallback when hooks aren't
    /// installed or when the gateway port is busy.
    /// </summary>
    /// <summary>
    /// Receives the raw JSON body of a Claude Code statusLine push routed by
    /// <see cref="HooksGateway"/>. CC pushes this every <c>refreshInterval</c>
    /// seconds (10s by default once the statusLine is wired). The JSON contains
    /// authoritative numbers — context %, rate-limit %, cost, lines added/
    /// removed, output style, vim mode, model display name — so we let it
    /// override the polled values from <see cref="TokenStats"/>.
    ///
    /// Schema reference: ccstatusline's <c>StatusJSONSchema</c> (Zod). See
    /// https://github.com/sirmalloc/ccstatusline/blob/main/src/types/StatusJSON.ts
    /// </summary>
    public void OnStatusLine(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return;
        // A statusLine push proves the gateway is alive — retire the OSC fallback.
        _monitor?.Detach();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawJson);
            var root = doc.RootElement;

            string? modelDisplay = null;
            if (root.TryGetProperty("model", out var modelEl))
            {
                if (modelEl.ValueKind == System.Text.Json.JsonValueKind.String)
                    modelDisplay = modelEl.GetString();
                else if (modelEl.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    if (modelEl.TryGetProperty("display_name", out var dn) && dn.ValueKind == System.Text.Json.JsonValueKind.String)
                        modelDisplay = dn.GetString();
                    else if (modelEl.TryGetProperty("id", out var idEl) && idEl.ValueKind == System.Text.Json.JsonValueKind.String)
                        modelDisplay = idEl.GetString();
                }
            }

            double? ctxPct = null;
            if (root.TryGetProperty("context_window", out var cw) && cw.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (cw.TryGetProperty("used_percentage", out var up) && TryReadDouble(up, out var upv)) ctxPct = upv;
                else if (cw.TryGetProperty("remaining_percentage", out var rp) && TryReadDouble(rp, out var rpv)) ctxPct = 100 - rpv;
            }

            double? cost = null;
            long linesAdded = 0, linesRemoved = 0;
            if (root.TryGetProperty("cost", out var costEl) && costEl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                if (costEl.TryGetProperty("total_cost_usd", out var tc) && TryReadDouble(tc, out var tcv)) cost = tcv;
                if (costEl.TryGetProperty("total_lines_added", out var la) && TryReadLong(la, out var lav)) linesAdded = lav;
                if (costEl.TryGetProperty("total_lines_removed", out var lr) && TryReadLong(lr, out var lrv)) linesRemoved = lrv;
            }

            double rl5 = 0, rl7 = 0;
            string rl5Reset = string.Empty, rl7Reset = string.Empty;
            DateTime? rl5ResetUtc = null;
            if (root.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                ReadRateLimit(rl, "five_hour", out rl5, out rl5Reset, out rl5ResetUtc);
                ReadRateLimit(rl, "seven_day", out rl7, out rl7Reset, out _);
            }

            string outputStyle = string.Empty;
            if (root.TryGetProperty("output_style", out var os) && os.ValueKind == System.Text.Json.JsonValueKind.Object
                && os.TryGetProperty("name", out var osn) && osn.ValueKind == System.Text.Json.JsonValueKind.String)
                outputStyle = osn.GetString() ?? string.Empty;

            string vimMode = string.Empty;
            if (root.TryGetProperty("vim", out var vm) && vm.ValueKind == System.Text.Json.JsonValueKind.Object
                && vm.TryGetProperty("mode", out var vmm) && vmm.ValueKind == System.Text.Json.JsonValueKind.String)
                vimMode = vmm.GetString() ?? string.Empty;

            string permissionMode = string.Empty;
            if (root.TryGetProperty("permission_mode", out var pm) && pm.ValueKind == System.Text.Json.JsonValueKind.String)
                permissionMode = pm.GetString() ?? string.Empty;

            string worktreeBranch = string.Empty;
            if (root.TryGetProperty("worktree", out var wt) && wt.ValueKind == System.Text.Json.JsonValueKind.Object
                && wt.TryGetProperty("branch", out var wtb) && wtb.ValueKind == System.Text.Json.JsonValueKind.String)
                worktreeBranch = wtb.GetString() ?? string.Empty;

            // Effort/thinking come straight from the statusLine push when CC includes them
            // (effort.level: "low"|"medium"|"high"|"xhigh"|"max", thinking.enabled: bool).
            // Authoritative — no transcript scrape needed when this is present.
            string? effortLevel = null;
            bool? thinkingEnabled = null;
            if (root.TryGetProperty("effort", out var eff) && eff.ValueKind == System.Text.Json.JsonValueKind.Object
                && eff.TryGetProperty("level", out var effl) && effl.ValueKind == System.Text.Json.JsonValueKind.String)
                effortLevel = effl.GetString();
            if (root.TryGetProperty("thinking", out var th) && th.ValueKind == System.Text.Json.JsonValueKind.Object
                && th.TryGetProperty("enabled", out var the)
                && (the.ValueKind == System.Text.Json.JsonValueKind.True || the.ValueKind == System.Text.Json.JsonValueKind.False))
                thinkingEnabled = the.GetBoolean();

            var app = System.Windows.Application.Current;
            void Apply()
            {
                if (modelDisplay is not null) ModelText = modelDisplay;
                if (ctxPct is { } p)
                {
                    CcContextPercent = p;
                    ContextPercentText = $"{(int)Math.Round(p)}%";
                }
                if (cost is { } c)
                {
                    CcCostUsd = c;
                    SessionCost = c;
                    CostText = c > 0 ? $"${c:0.00}" : string.Empty;
                }
                CcLinesAdded = linesAdded;
                CcLinesRemoved = linesRemoved;
                CcRateLimit5hPercent = rl5;
                CcRateLimit5hResetText = rl5Reset;
                CcRateLimit5hResetUtc = rl5ResetUtc;
                CcRateLimit7dPercent = rl7;
                CcRateLimit7dResetText = rl7Reset;
                CcOutputStyle = outputStyle;
                CcVimMode = vimMode;
                CcWorktreeBranch = worktreeBranch;
                CcPermissionMode = permissionMode;
                PermissionModeText = permissionMode switch
                {
                    "plan" => "plan mode",
                    "acceptEdits" => "auto-accept",
                    "bypassPermissions" => "bypass",
                    _ => string.Empty,
                };
                if (effortLevel is not null)
                {
                    EffortText = effortLevel;
                    _effortFromStatusLine = true;
                }
                else if (thinkingEnabled is false)
                {
                    EffortText = string.Empty;
                    _effortFromStatusLine = true;
                }
                CcLastUpdateUtc = DateTime.UtcNow;

                // Derived display strings.
                ContextPercentSubText = ctxPct is { } cp ? $"{(int)Math.Round(cp)}%" : string.Empty;
                RateLimit5hText = rl5 > 0
                    ? (string.IsNullOrEmpty(rl5Reset) ? $"5h {(int)Math.Round(rl5)}%" : $"5h {(int)Math.Round(rl5)}% · {rl5Reset}")
                    : string.Empty;
                RateLimit7dText = rl7 > 0
                    ? (string.IsNullOrEmpty(rl7Reset) ? $"7d {(int)Math.Round(rl7)}%" : $"7d {(int)Math.Round(rl7)}% · {rl7Reset}")
                    : string.Empty;
                LinesText = (linesAdded > 0 || linesRemoved > 0)
                    ? $"+{linesAdded:N0} / −{linesRemoved:N0}"
                    : string.Empty;
                StatusUpdatedText = CcLastUpdateUtc.ToLocalTime().ToString("HH:mm:ss");

                StatusLineUpdated?.Invoke(this, EventArgs.Empty);
            }
            if (app is null) return;
            if (app.Dispatcher.CheckAccess()) Apply();
            else app.Dispatcher.BeginInvoke((Action)Apply);
        }
        catch
        {
            // Malformed JSON — silently ignore. Next push will overwrite.
        }
    }

    private static void ReadRateLimit(System.Text.Json.JsonElement rl, string name, out double pct, out string resetText, out DateTime? resetUtc)
    {
        pct = 0;
        resetText = string.Empty;
        resetUtc = null;
        if (!rl.TryGetProperty(name, out var period) || period.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        if (period.TryGetProperty("used_percentage", out var up) && TryReadDouble(up, out var upv)) pct = upv;
        if (period.TryGetProperty("resets_at", out var ra) && TryReadDouble(ra, out var epoch))
        {
            // resets_at is Unix epoch seconds. Format as local HH:mm; if it's > 24h
            // away, switch to short date so a weekly reset doesn't read as a time today.
            try
            {
                var when = DateTimeOffset.FromUnixTimeSeconds((long)epoch);
                resetUtc = when.UtcDateTime;
                var local = when.ToLocalTime();
                var delta = when - DateTimeOffset.Now;
                resetText = delta.TotalHours >= 24
                    ? local.ToString("MMM d HH:mm")
                    : local.ToString("HH:mm");
            }
            catch { resetText = string.Empty; }
        }
    }

    private static bool TryReadDouble(System.Text.Json.JsonElement el, out double value)
    {
        value = 0;
        if (el.ValueKind == System.Text.Json.JsonValueKind.Number) return el.TryGetDouble(out value);
        if (el.ValueKind == System.Text.Json.JsonValueKind.String)
            return double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
        return false;
    }

    private static bool TryReadLong(System.Text.Json.JsonElement el, out long value)
    {
        value = 0;
        if (el.ValueKind == System.Text.Json.JsonValueKind.Number) return el.TryGetInt64(out value);
        if (el.ValueKind == System.Text.Json.JsonValueKind.String)
            return long.TryParse(el.GetString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);
        return false;
    }

    // Pulls permission_mode from a hook payload (UserPromptSubmit and friends carry
    // it as a top-level string) and pushes it through the same display switch the
    // statusLine path uses. Tolerates absent / malformed JSON — most events won't
    // include the field and that's fine, we just leave the previous value in place.
    private void TryApplyPermissionMode(string? rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return;
        string? mode = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawBody);
            if (doc.RootElement.TryGetProperty("permission_mode", out var pm)
                && pm.ValueKind == System.Text.Json.JsonValueKind.String)
            {
                mode = pm.GetString();
            }
        }
        catch { return; }

        if (mode is null) return;
        var text = mode switch
        {
            "plan" => "plan mode",
            "acceptEdits" => "auto-accept",
            "bypassPermissions" => "bypass",
            _ => string.Empty,
        };
        DispatchToUi(() =>
        {
            CcPermissionMode = mode;
            PermissionModeText = text;
        });
    }

    public void OnHookEvent(HookEvent evt)
    {
        // Hooks are the primary attention signal; once one arrives we know the
        // gateway → tab path is alive, so retire the OSC-title fallback to keep
        // the PTY read-loop free of regex work on every output chunk.
        _monitor?.Detach();

        // First hook event proves the Codex → Drover path is alive — defuse
        // the trusted-directory watchdog so a healthy install never toasts.
        CancelCodexHookWatchdog();

        // Same hook stream feeds the clean transcript writer. Boundary events
        // (UserPromptSubmit / PostToolUse / Stop) carry transcript_path; the
        // capturer tails CC's JSONL incrementally.
        _transcript?.OnHookEvent(evt);

        // Live subagent set for the Agents panel — Task/Agent tool launches and
        // returns, plus turn boundaries that clear everything.
        TrackSubagents(evt);

        // permission_mode isn't in CC's statusLine JSON yet, but the hook envelope
        // (UserPromptSubmit at minimum) carries it. Mirror it into the same fields
        // the statusLine path writes so the in-tab pill lights up regardless of
        // which signal got there first.
        TryApplyPermissionMode(evt.RawBody);

        // Codex has no statusLine; the hook envelope is the only signal carrying
        // `model`. Mirror it into both CodexModel (per spec, lets the simplified
        // pill bind without re-using the rich Claude path) and ModelText (so the
        // existing tab-header model badge lights up for free).
        if (evt.Kind == HookAgentKind.Codex && !string.IsNullOrWhiteSpace(evt.Model))
        {
            DispatchToUi(() =>
            {
                CodexModel = evt.Model;
                ModelText = evt.Model!;
            });
        }

        var next = evt.Type switch
        {
            "UserPromptSubmit" => AttentionState.Working,
            "PreToolUse"       => AttentionState.Working,
            "PostToolUse"      => AttentionState.Working,
            "SessionStart"     => AttentionState.Idle,
            // Stop = assistant turn finished, ready for next user input.
            "Stop"             => AttentionState.Idle,
            // SubagentStart/SubagentStop say nothing about the *main* agent —
            // background subagents outlive the turn, so a Stop-then-SubagentStop
            // sequence must not flip an idle tab back to Working (it would stick,
            // no follow-up event clears it). While the main agent is running its
            // own Pre/PostToolUse traffic keeps the tab Working anyway.
            "SubagentStop"     => (AttentionState?)null,
            // Notification = permission prompt or other user-attention signal.
            // Idle here is the right state for "needs user", the chrome treats
            // it the same as a finished turn (which is also "needs user").
            "Notification"     => AttentionState.Idle,
            _                  => (AttentionState?)null,
        };
        if (next is { } s) SetAttention(s);

        // Permission-prompt strip lifecycle. Notification arrives → set the
        // text; any other hook (Stop / UserPromptSubmit / a tool event) means
        // the prompt has been resolved or superseded, so clear it. Marshal to
        // UI thread because the gateway calls this from a background task.
        if (evt.Type == "Notification" && !IsIdleInputNudge(evt.Message))
        {
            var msg = string.IsNullOrWhiteSpace(evt.Message)
                ? "Claude needs your attention"
                : evt.Message!.Trim();
            DispatchToUi(() =>
            {
                PermissionPromptText = msg;
                PermissionPrompted?.Invoke(this, msg);
            });
        }
        else if (evt.Kind == HookAgentKind.Codex && evt.Type == "PermissionRequest")
        {
            // Codex emits PermissionRequest as its own dedicated event (no
            // Notification overloading like Claude). Surface tool name + a
            // truncated tool_input preview so the strip is informative.
            // Approve/deny stays inline in the terminal; Drover is read-only
            // until Phase 2 wires a forwarder-blocking response path.
            var tool = string.IsNullOrWhiteSpace(evt.Tool) ? "tool" : evt.Tool!;
            var preview = TruncateForStripPreview(evt.ToolInput, maxChars: 80);
            var msg = string.IsNullOrEmpty(preview)
                ? $"Codex needs permission to run {tool}"
                : $"Codex needs permission to run {tool}: {preview}";
            DispatchToUi(() =>
            {
                PermissionPromptText = msg;
                PermissionPrompted?.Invoke(this, msg);
            });
        }
        else if (next is not null && !string.IsNullOrEmpty(PermissionPromptText))
        {
            // First PreToolUse / PostToolUse / Stop / UserPromptSubmit after
            // a PermissionRequest clears the strip — same logic Claude uses.
            DispatchToUi(() => PermissionPromptText = string.Empty);
        }

        // Codex per-turn tool counter. Reset on UserPromptSubmit (new turn);
        // increment on PreToolUse so the counter reflects "tools attempted
        // this turn" and stays visible after Stop until the next prompt.
        if (evt.Kind == HookAgentKind.Codex)
        {
            if (evt.Type == "UserPromptSubmit")
            {
                DispatchToUi(() =>
                {
                    CodexToolCountThisTurn = 0;
                    CodexToolCountText = string.Empty;
                });
            }
            else if (evt.Type == "PreToolUse")
            {
                DispatchToUi(() =>
                {
                    CodexToolCountThisTurn += 1;
                    CodexToolCountText = CodexToolCountThisTurn == 1
                        ? "1 tool"
                        : $"{CodexToolCountThisTurn} tools";
                });
            }
        }

        // Live activity strip. Pre/PostToolUse carry tool_name; surface a
        // friendly one-liner ("Reading file…", "Running bash…") and (re)arm
        // the decay timer. Stop / SessionStart clear it immediately. Shared
        // across Claude and Codex — the spec's "reuse what Claude has" call.
        if (evt.Type == "PreToolUse" || evt.Type == "PostToolUse")
        {
            if (!string.IsNullOrWhiteSpace(evt.Tool))
            {
                var label = FriendlyToolLabel(evt.Tool!);
                DispatchToUi(() =>
                {
                    CurrentToolText = label;
                    ArmToolDecay();
                });
            }
        }
        else if (evt.Type == "Stop" || evt.Type == "SessionStart")
        {
            DispatchToUi(() =>
            {
                CurrentToolText = string.Empty;
                _toolDecayTimer?.Stop();
            });
        }
    }

    /// <summary>
    /// Truncates a raw <c>tool_input</c> JSON blob for display in the amber
    /// permission strip. Strips outer whitespace and collapses internal newlines
    /// so a multi-line shell command renders as one readable line. ≤80 chars
    /// per the spec; an ellipsis is appended when content was cut.
    /// </summary>
    private static string TruncateForStripPreview(string? raw, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var collapsed = System.Text.RegularExpressions.Regex
            .Replace(raw.Trim(), @"\s+", " ");
        return collapsed.Length <= maxChars ? collapsed : collapsed[..(maxChars - 1)] + "…";
    }

    // CC fires a Notification ~60s after Stop with "Claude is waiting for your input"
    // when the user hasn't typed anything. The Idle dot already conveys this and Stop
    // already raised a TaskbarFlash + idle toast, so the strip is redundant and stays
    // stuck until another hook arrives. Filter it out here.
    private static bool IsIdleInputNudge(string? message)
        => !string.IsNullOrEmpty(message)
           && message.Contains("waiting for your input", StringComparison.OrdinalIgnoreCase);

    private static string FriendlyToolLabel(string tool) => tool switch
    {
        "Read"          => "Reading file…",
        "Write"         => "Writing file…",
        "Edit"          => "Editing file…",
        "MultiEdit"     => "Editing file…",
        "NotebookEdit"  => "Editing notebook…",
        "Bash"          => "Running bash…",
        "PowerShell"    => "Running pwsh…",
        "Grep"          => "Searching code…",
        "Glob"          => "Finding files…",
        "WebFetch"      => "Fetching URL…",
        "WebSearch"     => "Searching web…",
        "Task"          => "Running subagent…",
        "Agent"         => "Running subagent…",
        "TodoWrite"     => "Updating todos…",
        _               => $"Using {tool}…",
    };

    private void ArmToolDecay()
    {
        // Must run on UI thread — DispatcherTimer is dispatcher-affine.
        if (_toolDecayTimer is null)
        {
            _toolDecayTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _toolDecayTimer.Tick += (_, _) =>
            {
                _toolDecayTimer?.Stop();
                CurrentToolText = string.Empty;
            };
        }
        _toolDecayTimer.Stop();
        _toolDecayTimer.Start();
    }

    private static void DispatchToUi(Action a)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) a();
        else app.Dispatcher.BeginInvoke(a);
    }

    private void SetAttention(AttentionState s)
    {
        var app = System.Windows.Application.Current;
        void Apply()
        {
            var previous = Attention;
            Attention = s;
            if (s != AttentionState.Unknown) AttentionStale = false;
            UpdateWorkingTimer(s);
            AttentionChanged?.Invoke(this, (previous, s));
        }
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) Apply();
        else app.Dispatcher.BeginInvoke((Action)Apply);
    }

    public void UpdateMetrics(string? model, long contextTokens, long contextLimit, double cost, string? effort = null)
    {
        var app = System.Windows.Application.Current;
        void Apply()
        {
            ModelText = model ?? string.Empty;
            // Don't clobber a statusLine-pushed effort with the transcript-derived one —
            // CC's push is authoritative once received.
            if (!_effortFromStatusLine)
                EffortText = effort ?? string.Empty;
            ContextTokens = contextTokens;
            SessionCost = cost;
            // statusLine is authoritative for context %. Only fall back to the
            // transcript-derived value when no statusLine push has landed yet —
            // otherwise the two sources flip-flop because they use different
            // denominators (CC measures against the auto-compact threshold).
            if (CcLastUpdateUtc == default)
            {
                ContextPercentText = contextLimit > 0
                    ? $"{Math.Min(100, (int)(contextTokens * 100.0 / contextLimit))}%"
                    : string.Empty;
            }
            CostText = cost > 0 ? $"${cost:0.00}" : string.Empty;
        }
        if (app is null) return;
        if (app.Dispatcher.CheckAccess()) Apply();
        else app.Dispatcher.BeginInvoke((Action)Apply);
    }

    /// <summary>
    /// Exposes the live ConPTY connection for callers that need to subscribe
    /// to the output stream (e.g. the session-driven loop runner parsing
    /// <c>[loop:*]</c> tokens). Returns null until the terminal control has
    /// finished its async attach. The caller must tolerate a later-arriving
    /// connection by re-calling on each interaction.
    /// </summary>
    public ConPtyConnection? GetCurrentConnection() => _control?.Connection;

    /// <summary>
    /// True while this tab's terminal is reparented into a <see cref="Views.PoppedOutWindow"/>.
    /// A popped-out terminal is on screen even when it isn't the selected tab, so
    /// <see cref="ShellViewModel.SyncConnectionThrottles"/> must not throttle its output.
    /// </summary>
    public bool IsPoppedOut { get; set; }

    public bool SendInput(string text, bool appendReturn = true)
    {
        var control = _control;
        var pty = control?.Connection;
        if (pty is null) return false;

        // Multi-line input must be wrapped in bracketed-paste markers, mirroring
        // ClipboardIntegration's Ctrl+V path. Without them, Claude Code's TUI
        // interprets each embedded newline as Enter and submits the prompt
        // piecemeal — the user only sees the trailing fragment as a message.
        string payload;
        if (text.IndexOf('\n') >= 0 || text.IndexOf('\r') >= 0)
        {
            var normalized = text.Replace("\r\n", "\r").Replace("\n", "\r");
            payload = "\x1b[200~" + normalized + "\x1b[201~" + (appendReturn ? "\r" : string.Empty);
        }
        else
        {
            payload = appendReturn ? text + "\r" : text;
        }

        try { pty.WriteToTerm(payload); }
        catch { return false; }
        FocusTerminal(control);
        return true;
    }

    /// <summary>Moves keyboard focus into this tab's terminal (deferred to Input priority).</summary>
    public void FocusTerminal() => FocusTerminal(_control);

    // Move WPF keyboard focus to the terminal so a button-click that triggered
    // SendInput doesn't leave the originating button focused — otherwise the
    // user's next Enter re-fires that button's command (re-pasting the prompt).
    // Defer to DispatcherPriority.Input so it lands after the click completes.
    private static void FocusTerminal(DroverTerminal? control)
    {
        if (control is null) return;
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(new Action(() => control.Terminal.Focus()),
            DispatcherPriority.Input);
    }

    private void UpdateWorkingTimer(AttentionState state)
    {
        if (state == AttentionState.Working)
        {
            if (_workingSince is null)
            {
                _workingSince = DateTime.UtcNow;
                RefreshElapsed();
                _workingTimer ??= new DispatcherTimer(DispatcherPriority.Background)
                {
                    Interval = TimeSpan.FromSeconds(1)
                };
                _workingTimer.Tick -= OnWorkingTick;
                _workingTimer.Tick += OnWorkingTick;
                _workingTimer.Start();
            }
        }
        else
        {
            _workingSince = null;
            WorkingElapsedText = string.Empty;
            _workingTimer?.Stop();
        }
    }

    private void OnWorkingTick(object? sender, EventArgs e) => RefreshElapsed();

    private void RefreshElapsed()
    {
        if (_workingSince is null) { WorkingElapsedText = string.Empty; return; }
        var d = DateTime.UtcNow - _workingSince.Value;
        WorkingElapsedText = d.TotalHours >= 1
            ? $"{(int)d.TotalHours}:{d.Minutes:D2}:{d.Seconds:D2}"
            : $"{d.Minutes}:{d.Seconds:D2}";
    }

    public void Dispose()
    {
        _workingTimer?.Stop();
        _workingTimer = null;
        _toolDecayTimer?.Stop();
        _toolDecayTimer = null;
        _codexHookWatchdog?.Stop();
        _codexHookWatchdog = null;
        _logger?.Dispose();
        _logger = null;
        _transcript?.Dispose();
        _transcript = null;
        _monitor = null;

        // Terminate the PTY explicitly so closing a tab mid-stream tears the
        // child process down rather than relying on later GC of the control.
        var pty = _control?.Connection;
        if (pty is not null)
        {
            try { pty.CloseStdin(); } catch { }
            try { pty.KillProcess(); } catch { }
        }
        _control = null;
    }
}
