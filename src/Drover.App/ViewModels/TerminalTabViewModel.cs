using System;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Threading;
using Drover.App.Models;
using Drover.App.Services;
using CommunityToolkit.Mvvm.ComponentModel;
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
    [ObservableProperty] private double _ccRateLimit7dPercent;
    [ObservableProperty] private string _ccRateLimit7dResetText = string.Empty;
    [ObservableProperty] private long _ccLinesAdded;
    [ObservableProperty] private long _ccLinesRemoved;
    [ObservableProperty] private double _ccCostUsd;
    [ObservableProperty] private string _ccWorktreeBranch = string.Empty;
    [ObservableProperty] private DateTime _ccLastUpdateUtc;

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

    // True once a statusLine push has supplied an authoritative effort.level (or a
    // thinking.enabled=false). Polled transcript scanning won't overwrite EffortText
    // after this — CC's push is the source of truth.
    private bool _effortFromStatusLine;

    private AttentionMonitor? _monitor;
    private ClipboardIntegration? _clipboard;
    private SessionLogger? _logger;
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
        System.Collections.Generic.IReadOnlyDictionary<string, string>? globalEnvVars = null,
        Drover.App.Services.SessionLogOptions? logOptions = null)
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
        GlobalEnvVars = globalEnvVars;
        LogOptions = logOptions;
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
    public System.Collections.Generic.IReadOnlyDictionary<string, string>? GlobalEnvVars { get; }
    public Drover.App.Services.SessionLogOptions? LogOptions { get; }
    public string SessionId { get; }

    /// <summary>
    /// Frozen <see cref="SolidColorBrush"/> derived from <see cref="ProjectDefinition.TabColor"/>,
    /// or null when no per-project colour is set. The tab header style binds to this and falls
    /// back to the chrome default when null. Returning null on parse failure keeps a typo'd hex
    /// from breaking the UI — the tab just renders with the default chrome.
    /// </summary>
    public Brush? TabColorBrush
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Project.TabColor)) return null;
            try
            {
                var color = (Color)ColorConverter.ConvertFromString(Project.TabColor)!;
                var b = new SolidColorBrush(color);
                b.Freeze();
                return b;
            }
            catch { return null; }
        }
    }

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
    /// Forces the background session-log writer to drain pending chunks and
    /// flush the StreamWriter. Used by <see cref="HistoryExporter"/> so the
    /// exported markdown reflects the current on-screen state, not whatever
    /// happened to be flushed up to ~250 ms ago.
    /// </summary>
    public void FlushSessionLog() => _logger?.Flush();

    /// <summary>True for Claude-flavored kinds (Claude, ClaudeDirect) — controls whether
    /// CLAUDE_CODE_NO_FLICKER, --model/--resume/--dangerously-skip-permissions, and the
    /// implicit "claude" command apply.</summary>
    private bool IsClaudeKind => Project.Kind is ProjectKind.Claude or ProjectKind.ClaudeDirect;

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
                sb.Append("$env:CLAUDE_CODE_NO_FLICKER=1; ");

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

        _logger = new SessionLogger(control, Title, LogOptions);
        _logger.Attach();

        _monitor = new AttentionMonitor(control);
        _monitor.StateChanged += (_, s) => SetAttention(s);
        _monitor.Attach();

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
    /// Fires after a Claude Code statusLine push has been parsed and the Cc* fields
    /// updated. Lets the shell aggregate global state (e.g. account-wide rate limits)
    /// across tabs without polling. Always raised on the UI dispatcher.
    /// </summary>
    public event EventHandler? StatusLineUpdated;

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
            if (root.TryGetProperty("rate_limits", out var rl) && rl.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                ReadRateLimit(rl, "five_hour", out rl5, out rl5Reset);
                ReadRateLimit(rl, "seven_day", out rl7, out rl7Reset);
            }

            string outputStyle = string.Empty;
            if (root.TryGetProperty("output_style", out var os) && os.ValueKind == System.Text.Json.JsonValueKind.Object
                && os.TryGetProperty("name", out var osn) && osn.ValueKind == System.Text.Json.JsonValueKind.String)
                outputStyle = osn.GetString() ?? string.Empty;

            string vimMode = string.Empty;
            if (root.TryGetProperty("vim", out var vm) && vm.ValueKind == System.Text.Json.JsonValueKind.Object
                && vm.TryGetProperty("mode", out var vmm) && vmm.ValueKind == System.Text.Json.JsonValueKind.String)
                vimMode = vmm.GetString() ?? string.Empty;

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
                CcRateLimit7dPercent = rl7;
                CcRateLimit7dResetText = rl7Reset;
                CcOutputStyle = outputStyle;
                CcVimMode = vimMode;
                CcWorktreeBranch = worktreeBranch;
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

    private static void ReadRateLimit(System.Text.Json.JsonElement rl, string name, out double pct, out string resetText)
    {
        pct = 0;
        resetText = string.Empty;
        if (!rl.TryGetProperty(name, out var period) || period.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        if (period.TryGetProperty("used_percentage", out var up) && TryReadDouble(up, out var upv)) pct = upv;
        if (period.TryGetProperty("resets_at", out var ra) && TryReadDouble(ra, out var epoch))
        {
            // resets_at is Unix epoch seconds. Format as local HH:mm; if it's > 24h
            // away, switch to short date so a weekly reset doesn't read as a time today.
            try
            {
                var when = DateTimeOffset.FromUnixTimeSeconds((long)epoch).ToLocalTime();
                var delta = when - DateTimeOffset.Now;
                resetText = delta.TotalHours >= 24
                    ? when.ToString("MMM d HH:mm")
                    : when.ToString("HH:mm");
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

    public void OnHookEvent(HookEvent evt)
    {
        // Hooks are the primary attention signal; once one arrives we know the
        // gateway → tab path is alive, so retire the OSC-title fallback to keep
        // the PTY read-loop free of regex work on every output chunk.
        _monitor?.Detach();

        var next = evt.Type switch
        {
            "UserPromptSubmit" => AttentionState.Working,
            "PreToolUse"       => AttentionState.Working,
            "PostToolUse"      => AttentionState.Working,
            "SessionStart"     => AttentionState.Idle,
            // Stop = assistant turn finished, ready for next user input.
            "Stop"             => AttentionState.Idle,
            // SubagentStop fires when a Task subagent returns; main agent is
            // still running, so treat it like activity.
            "SubagentStop"     => AttentionState.Working,
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
        if (evt.Type == "Notification")
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
        else if (next is not null && !string.IsNullOrEmpty(PermissionPromptText))
        {
            DispatchToUi(() => PermissionPromptText = string.Empty);
        }

        // Live activity strip. Pre/PostToolUse carry tool_name; surface a
        // friendly one-liner ("Reading file…", "Running bash…") and (re)arm
        // the decay timer. Stop / SessionStart clear it immediately.
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

    public bool SendInput(string text, bool appendReturn = true)
    {
        var control = _control;
        var pty = control?.Connection;
        if (pty is null) return false;
        var payload = appendReturn ? text + "\r" : text;
        try { pty.WriteToTerm(payload); }
        catch { return false; }
        FocusTerminal(control);
        return true;
    }

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
        _logger?.Dispose();
        _logger = null;
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
