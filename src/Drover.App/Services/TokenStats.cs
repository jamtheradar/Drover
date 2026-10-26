using Drover.App.ViewModels;
using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Drover.App.Services;

public sealed record DailyPoint(DateTime Date, double Cost, long Input, long Output, long Cache);
public sealed record ProjectTotals(string Project, double Cost, long Tokens);
public sealed record SessionRow(DateTime LastWriteUtc, string Project, string? Model, double Cost, long Tokens);

/// <summary>
/// Lightweight tokenomics: scans Claude Code's per-project transcript JSONL files
/// (%USERPROFILE%\.claude\projects\&lt;sanitized-path&gt;\*.jsonl), parses usage blocks,
/// and exposes per-tab tokens / cost plus a daily aggregate. Polling-based — no
/// realtime hook needed. Read-only side; never writes Claude's data.
/// </summary>
public sealed class TokenStats
{
    private static readonly JsonDocumentOptions JsonOpts = new() { AllowTrailingCommas = true };

    // Matches `"budget_tokens":12345` and the JSON-escaped form `\"budget_tokens\":12345`
    // (CC stores the request body as an escaped string, so budget_tokens appears escaped).
    private static readonly System.Text.RegularExpressions.Regex BudgetTokensRx =
        new(@"\\?""budget_tokens\\?""\s*:\s*(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);

    // Best-effort pricing per 1M tokens (USD) for common Claude models. Updated 2026-07.
    // Cache read = 0.1× input, cache write = 1.25× input (5-minute TTL).
    // We fail-soft to zero if we don't recognise the model; cost is informational only.
    private static readonly Dictionary<string, (double input, double output, double cacheRead, double cacheWrite)> Pricing =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-fable-5"] = (10, 50, 1.0, 12.5),
            ["claude-mythos-5"] = (10, 50, 1.0, 12.5),
            ["claude-opus-4-8"] = (5, 25, 0.5, 6.25),
            ["claude-opus-4-7"] = (5, 25, 0.5, 6.25),
            ["claude-opus-4-6"] = (5, 25, 0.5, 6.25),
            ["claude-opus-4-5"] = (5, 25, 0.5, 6.25),
            // Opus 4.0/4.1 were the last $15/$75 Opus models.
            ["claude-opus-4"] = (15, 75, 1.5, 18.75),
            // Sonnet 5 lists at $3/$15; an intro rate of $2/$10 applies through 2026-08-31.
            ["claude-sonnet-5"] = (3, 15, 0.3, 3.75),
            ["claude-sonnet-4-6"] = (3, 15, 0.3, 3.75),
            ["claude-sonnet-4-5"] = (3, 15, 0.3, 3.75),
            ["claude-sonnet-4"] = (3, 15, 0.3, 3.75),
            ["claude-haiku-4-5"] = (1, 5, 0.1, 1.25),
        };

    private const long DefaultContextLimit = 200_000;
    // Opus 4.7 1M context tier — model name carries the [1m] suffix in the API id.
    private const long ExtendedContextLimit = 1_000_000;

    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _timer;
    private DateTime _lastDayKey = DateTime.MinValue;

    // Refresh() sits on a hot path — Claude Code pushes a statusLine per session every
    // few seconds and every push asks for a rescan — while the scan itself walks every
    // transcript JSONL under ~/.claude/projects. Debounce, and never run two at once.
    private static readonly TimeSpan MinScanInterval = TimeSpan.FromSeconds(10);
    private const int MaxEntriesPerFile = 50_000;
    private static readonly List<UsageEntry> EmptyEntries = new();
    private int _scanning;
    private DateTime _lastScanStartUtc = DateTime.MinValue;

    // Per-file scan cache keyed by full path, invalidated on (mtime, length). Transcripts
    // are append-only and every file but the live one is immutable, so a warm cache turns
    // a full-history walk into re-reading whichever session just wrote.
    private readonly Dictionary<string, FileScan> _fileCache = new(StringComparer.OrdinalIgnoreCase);

    public double DailyCostUsd { get; private set; }
    public long DailyInputTokens { get; private set; }
    public long DailyOutputTokens { get; private set; }
    public long DailyCacheTokens { get; private set; }
    public IReadOnlyList<(string Model, double Cost)> DailyByModel { get; private set; } = Array.Empty<(string, double)>();

    // Rolling 5-hour usage window (Claude Code Pro/Max session limit window). Window start =
    // earliest usage timestamp in the last 5h; reset = start + 5h. Null when no usage in window.
    public static readonly TimeSpan SessionWindowLength = TimeSpan.FromHours(5);
    public DateTime? SessionWindowStartUtc { get; private set; }
    public DateTime? SessionResetUtc { get; private set; }
    public double SessionCostUsd { get; private set; }
    public long SessionInputTokens { get; private set; }
    public long SessionOutputTokens { get; private set; }
    public long SessionCacheTokens { get; private set; }

    // Extended aggregates for the Tokenomics view.
    public double Last7DaysCostUsd { get; private set; }
    public double Last30DaysCostUsd { get; private set; }
    public double AllTimeCostUsd { get; private set; }
    public long AllTimeInputTokens { get; private set; }
    public long AllTimeOutputTokens { get; private set; }
    public long AllTimeCacheTokens { get; private set; }
    public IReadOnlyList<DailyPoint> DailySeries { get; private set; } = Array.Empty<DailyPoint>();
    public IReadOnlyList<ProjectTotals> ProjectTotals7d { get; private set; } = Array.Empty<ProjectTotals>();
    public IReadOnlyList<(string Model, double Cost)> ModelByCost7d { get; private set; } = Array.Empty<(string, double)>();
    public IReadOnlyList<SessionRow> RecentSessions { get; private set; } = Array.Empty<SessionRow>();

    public event EventHandler? Updated;

    public TokenStats(ShellViewModel shell)
    {
        _shell = shell;
        _timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(15)
        };
        _timer.Tick += (_, _) => Refresh(force: true);
    }

    public void Start()
    {
        Refresh(force: true);
        _timer.Start();
    }

    /// <summary>
    /// Requests a scan. Safe to call from hot paths — every statusLine push on every
    /// tab lands here — because the transcript walk is debounced to
    /// <see cref="MinScanInterval"/>, coalesced to one in-flight scan, and runs on a
    /// worker thread. Only <see cref="Apply"/> touches the UI thread.
    /// </summary>
    public void Refresh() => Refresh(force: false);

    /// <summary>
    /// <paramref name="force"/> bypasses the debounce. Used by <see cref="Start"/> and
    /// the polling timer, which already runs on its own cadence.
    /// </summary>
    public void Refresh(bool force)
    {
        if (!force && DateTime.UtcNow - _lastScanStartUtc < MinScanInterval) return;
        StartScan();
    }

    private void StartScan()
    {
        // One scan at a time. A few months of history is hundreds of MB of JSONL;
        // queuing scans would just pile worker threads onto the same disk.
        if (Interlocked.CompareExchange(ref _scanning, 1, 0) != 0) return;

        _lastScanStartUtc = DateTime.UtcNow;

        // Snapshot the tab set on the calling (UI) thread so the worker never touches
        // the ObservableCollection or any ViewModel state.
        var tabsByPath = SnapshotTabsByPath();

        Task.Run(() =>
        {
            ScanResult? result = null;
            try { result = ScanCore(tabsByPath); }
            catch { /* never propagate from a background poll */ }
            finally { Interlocked.Exchange(ref _scanning, 0); }

            if (result is null) return;
            var app = System.Windows.Application.Current;
            if (app is null) return;
            app.Dispatcher.BeginInvoke(new Action(() => Apply(result)), DispatcherPriority.Background);
        });
    }

    private Dictionary<string, List<TerminalTabViewModel>> SnapshotTabsByPath()
    {
        var map = new Dictionary<string, List<TerminalTabViewModel>>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _shell.Tabs)
        {
            if (!map.TryGetValue(t.Project.Path, out var list)) map[t.Project.Path] = list = new();
            list.Add(t);
        }
        if (_shell.SecondaryTab is { } sec)
        {
            if (!map.TryGetValue(sec.Project.Path, out var list)) map[sec.Project.Path] = list = new();
            list.Add(sec);
        }
        return map;
    }

    /// <summary>
    /// Walks every transcript JSONL under <c>~/.claude/projects</c> and aggregates it.
    /// Worker thread only — takes a pre-snapshotted tab map and returns a plain result
    /// record for <see cref="Apply"/> to push into ViewModel state.
    /// </summary>
    private ScanResult? ScanCore(Dictionary<string, List<TerminalTabViewModel>> tabsByPath)
    {
        var root = ClaudeProjectsRoot();
        if (root is null || !Directory.Exists(root)) return null;

        var today = DateTime.UtcNow.Date;

        double dayCost = 0;
        long dayIn = 0, dayOut = 0, dayCache = 0;
        var byModelToday = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // Rolling 5h usage window — collect every usage line in the last 24h, then walk
        // them in time order after the file scan to find the first message of the *current*
        // window. Anthropic anchors each 5h window on its first message; "earliest in last
        // 5h" is wrong when a previous window straddles the boundary.
        var nowUtc = DateTime.UtcNow;
        var sessionScanEdge = nowUtc - TimeSpan.FromHours(24);
        var sessionEntries = new List<UsageEntry>(256);

        // Extended aggregates
        double cost7 = 0, cost30 = 0, costAll = 0;
        long inAll = 0, outAll = 0, cacheAll = 0;
        var dailyMap = new Dictionary<DateTime, DailyPoint>();
        for (int i = 13; i >= 0; i--)
        {
            var d = today.AddDays(-i);
            dailyMap[d] = new DailyPoint(d, 0, 0, 0, 0);
        }
        var byProject7 = new Dictionary<string, (double cost, long tokens)>(StringComparer.OrdinalIgnoreCase);
        var byModel7 = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var sessions = new List<SessionRow>(256);
        var tabMetrics = new List<TabMetrics>();
        var sevenDaysAgo = today.AddDays(-6);
        var thirtyDaysAgo = today.AddDays(-29);
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var match = MatchProjectDir(dir, tabsByPath);
            var projectLabel = ProjectLabelFromDir(dir);

            // Per-tab metrics track the most recently written transcript in this project dir.
            // Picking by mtime (not by highest token count) ensures the badge reflects the
            // session the user is actually in, even when older heavier sessions exist.
            string? sessionModel = null;
            long sessionContext = 0;
            double sessionCost = 0;
            long sessionBudget = 0;
            bool sessionHadThinking = false;
            DateTime sessionMtime = DateTime.MinValue;
            // Fallback model from any earlier file, used if the most-recent file's lines
            // don't include a model field (e.g. tool-result-only writes).
            string? fallbackModel = null;

            foreach (var path in Directory.EnumerateFiles(dir, "*.jsonl"))
            {
                seenFiles.Add(path);
                var scan = ScanFile(path, sessionScanEdge);
                if (scan is null) continue;

                var model = scan.Model;
                var fileCost = scan.Cost;
                var fileTokens = scan.InputTokens + scan.OutputTokens + scan.CacheRead + scan.CacheWrite;
                var fileDate = scan.WriteUtc.Date;

                // Entries were captured when the file was inside the 24h edge; re-filter
                // because a cached file's entries age out without the file changing.
                foreach (var e in scan.Entries)
                    if (e.Ts >= sessionScanEdge) sessionEntries.Add(e);

                // All-time
                costAll += fileCost;
                inAll += scan.InputTokens;
                outAll += scan.OutputTokens;
                cacheAll += scan.CacheRead + scan.CacheWrite;

                // 30-day window
                if (fileDate >= thirtyDaysAgo)
                    cost30 += fileCost;

                // 7-day window
                if (fileDate >= sevenDaysAgo)
                {
                    cost7 += fileCost;
                    byProject7.TryGetValue(projectLabel, out var prev);
                    byProject7[projectLabel] = (prev.cost + fileCost, prev.tokens + fileTokens);
                    var modelKey = model ?? "unknown";
                    byModel7.TryGetValue(modelKey, out var mp);
                    byModel7[modelKey] = mp + fileCost;
                }

                // Daily series (last 14 days)
                if (dailyMap.TryGetValue(fileDate, out var pt))
                {
                    dailyMap[fileDate] = pt with
                    {
                        Cost = pt.Cost + fileCost,
                        Input = pt.Input + scan.InputTokens,
                        Output = pt.Output + scan.OutputTokens,
                        Cache = pt.Cache + scan.CacheRead + scan.CacheWrite
                    };
                }

                // Today
                if (fileDate == today)
                {
                    dayCost += fileCost;
                    dayIn += scan.InputTokens;
                    dayOut += scan.OutputTokens;
                    dayCache += scan.CacheRead + scan.CacheWrite;
                    var modelLabel = model ?? "unknown";
                    byModelToday.TryGetValue(modelLabel, out var prev);
                    byModelToday[modelLabel] = prev + fileCost;
                }

                // Recent sessions (any session with usage in the last 30 days)
                if (fileDate >= thirtyDaysAgo && fileTokens > 0)
                    sessions.Add(new SessionRow(scan.WriteUtc, projectLabel, model, fileCost, fileTokens));

                if (match is not null)
                {
                    if (model is not null) fallbackModel = model;
                    if (scan.WriteUtc > sessionMtime)
                    {
                        sessionMtime = scan.WriteUtc;
                        sessionModel = model;
                        sessionContext = scan.LastInputForContext;
                        sessionBudget = scan.Budget;
                        sessionHadThinking = scan.HasThinking;
                        sessionCost = fileCost;
                    }
                }
            }

            if (match is not null)
            {
                // If the most recent file had no model field, use the latest one we did see.
                var modelOut = sessionModel ?? fallbackModel;
                var limit = (modelOut is not null && modelOut.Contains("[1m]", StringComparison.OrdinalIgnoreCase))
                    ? ExtendedContextLimit : DefaultContextLimit;
                var effort = EffortLabel(sessionBudget, sessionHadThinking);
                tabMetrics.Add(new TabMetrics(match, modelOut, sessionContext, limit, sessionCost, effort));
            }
        }

        PruneFileCache(seenFiles);

        // Walk usage entries in time order to find the first message of the *current*
        // 5h window. A new window opens when the gap from the last accepted message
        // is >= 5h. The current window is whichever the most recent message falls into.
        sessionEntries.Sort((a, b) => a.Ts.CompareTo(b.Ts));
        DateTime? curStart = null;
        foreach (var e in sessionEntries)
        {
            if (curStart is null || e.Ts >= curStart.Value + SessionWindowLength)
                curStart = e.Ts;
        }
        // If the current window has already elapsed (no message in last 5h), we have no
        // active window — the next message will open a fresh one.
        if (curStart is { } cs && nowUtc >= cs + SessionWindowLength) curStart = null;

        double winCost = 0;
        long winIn = 0, winOut = 0, winCache = 0;
        if (curStart is { } start)
        {
            foreach (var e in sessionEntries)
            {
                if (e.Ts < start) continue;
                winCost += e.Cost;
                winIn += e.Input;
                winOut += e.Output;
                winCache += e.Cache;
            }
        }

        return new ScanResult(
            today,
            dayCost, dayIn, dayOut, dayCache,
            byModelToday.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList(),
            curStart, winCost, winIn, winOut, winCache,
            cost7, cost30, costAll,
            inAll, outAll, cacheAll,
            dailyMap.Values.OrderBy(p => p.Date).ToList(),
            byProject7.OrderByDescending(kv => kv.Value.cost)
                .Select(kv => new ProjectTotals(kv.Key, kv.Value.cost, kv.Value.tokens)).ToList(),
            byModel7.OrderByDescending(kv => kv.Value).Select(kv => (kv.Key, kv.Value)).ToList(),
            sessions.OrderByDescending(s => s.LastWriteUtc).Take(20).ToList(),
            tabMetrics);
    }

    /// <summary>UI thread. Publishes a completed scan and notifies subscribers.</summary>
    private void Apply(ScanResult r)
    {
        _lastDayKey = r.Today;

        SessionCostUsd = r.SessionCost;
        SessionInputTokens = r.SessionIn;
        SessionOutputTokens = r.SessionOut;
        SessionCacheTokens = r.SessionCache;
        SessionWindowStartUtc = r.SessionWindowStartUtc;
        SessionResetUtc = r.SessionWindowStartUtc is { } s ? s + SessionWindowLength : null;

        DailyCostUsd = r.DayCost;
        DailyInputTokens = r.DayIn;
        DailyOutputTokens = r.DayOut;
        DailyCacheTokens = r.DayCache;
        DailyByModel = r.ByModelToday;

        Last7DaysCostUsd = r.Cost7;
        Last30DaysCostUsd = r.Cost30;
        AllTimeCostUsd = r.CostAll;
        AllTimeInputTokens = r.InAll;
        AllTimeOutputTokens = r.OutAll;
        AllTimeCacheTokens = r.CacheAll;
        DailySeries = r.DailySeries;
        ProjectTotals7d = r.ProjectTotals7d;
        ModelByCost7d = r.ModelByCost7d;
        RecentSessions = r.RecentSessions;

        foreach (var tm in r.TabMetrics)
            foreach (var tab in tm.Tabs)
                tab.UpdateMetrics(tm.Model, tm.Context, tm.Limit, tm.Cost, tm.Effort);

        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Returns the cached scan for <paramref name="path"/> when the file is unchanged,
    /// otherwise re-reads it. Transcripts are append-only and all but the live one are
    /// immutable, so a warm cache turns a full-history walk into re-reading one file.
    /// </summary>
    private FileScan? ScanFile(string path, DateTime sessionScanEdge)
    {
        DateTime writeUtc;
        long length;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;
            writeUtc = info.LastWriteTimeUtc;
            length = info.Length;
        }
        catch { return null; }

        lock (_fileCache)
        {
            if (_fileCache.TryGetValue(path, out var cached)
                && cached.WriteUtc == writeUtc && cached.Length == length)
                return cached;
        }

        var scan = ReadFile(path, writeUtc, sessionScanEdge);
        if (scan is null) return null;
        lock (_fileCache) _fileCache[path] = scan;
        return scan;
    }

    private void PruneFileCache(HashSet<string> seen)
    {
        lock (_fileCache)
        {
            if (_fileCache.Count <= seen.Count) return;
            foreach (var key in _fileCache.Keys.Where(k => !seen.Contains(k)).ToList())
                _fileCache.Remove(key);
        }
    }

    private static FileScan? ReadFile(string path, DateTime writeUtc, DateTime sessionScanEdge)
    {
        long inTok = 0, outTok = 0, cRead = 0, cWrite = 0;
        string? model = null;
        long lastInputForContext = 0;
        long fileBudget = 0;
        bool fileHasThinking = false;
        long length;

        // Skip per-line timestamp capture entirely if the whole file is older than the scan edge.
        var maybeInScan = writeUtc >= sessionScanEdge;
        var entries = maybeInScan ? new List<UsageEntry>(64) : EmptyEntries;

        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            length = fs.Length;
            using var sr = new StreamReader(fs);
            string? line;
            while ((line = sr.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                // Cheap upfront detection: a `"type":"thinking"` substring is a sufficient
                // signal that a thinking block is present on this line. Avoids the cost of
                // full JSON-parsing every assistant turn (transcript lines can be huge) and
                // sidesteps any future schema drift around where budget_tokens lives.
                if (line.IndexOf("\"type\":\"thinking\"", StringComparison.Ordinal) >= 0)
                    fileHasThinking = true;

                // JsonDocument.Parse per line over the whole history is by far the most
                // expensive thing this class does, so every parse is gated on a literal the
                // line must contain for the parse to find anything. Budget often appears
                // inside the request body which CC stores as an escaped JSON string, so the
                // literal `"budget_tokens":N` is JSON-escaped to `\"budget_tokens\":N`. The
                // regex tolerates both forms and keeps the largest budget seen on this line.
                if (line.IndexOf("budget_tokens", StringComparison.Ordinal) >= 0)
                {
                    if (fileBudget == 0)
                    {
                        var bm = BudgetTokensRx.Match(line);
                        if (bm.Success && long.TryParse(bm.Groups[1].Value, out var bv) && bv > 0)
                            fileBudget = bv;
                    }
                    if (TryReadEffort(line, out var budget, out var hadThinking))
                    {
                        if (budget > 0) fileBudget = budget;
                        if (hadThinking) fileHasThinking = true;
                    }
                }

                // TryReadUsage returns false without a usage object, so lines that can't
                // carry one are skipped before paying for a parse.
                if (line.IndexOf("\"usage\"", StringComparison.Ordinal) < 0) continue;
                if (!TryReadUsage(line, out var u, out var m, out var ts)) continue;
                if (m is not null) model = m;
                inTok += u.input;
                outTok += u.output;
                cRead += u.cacheRead;
                cWrite += u.cacheWrite;
                lastInputForContext = u.input + u.cacheRead + u.cacheWrite;

                if (maybeInScan && ts is { } tsv && tsv >= sessionScanEdge
                    && entries.Count < MaxEntriesPerFile)
                {
                    var entryCost = CostFor(m ?? model, u.input, u.output, u.cacheRead, u.cacheWrite);
                    entries.Add(new UsageEntry(tsv, entryCost, u.input, u.output, u.cacheRead + u.cacheWrite));
                }
            }
        }
        catch { return null; }

        var cost = CostFor(model, inTok, outTok, cRead, cWrite);
        return new FileScan(writeUtc, length, inTok, outTok, cRead, cWrite,
            model, cost, lastInputForContext, fileBudget, fileHasThinking, entries);
    }

    private readonly record struct UsageEntry(DateTime Ts, double Cost, long Input, long Output, long Cache);

    /// <summary>Cached aggregate for one transcript file, keyed on (mtime, length).</summary>
    private sealed record FileScan(
        DateTime WriteUtc, long Length,
        long InputTokens, long OutputTokens, long CacheRead, long CacheWrite,
        string? Model, double Cost, long LastInputForContext,
        long Budget, bool HasThinking, List<UsageEntry> Entries);

    private sealed record TabMetrics(
        List<TerminalTabViewModel> Tabs, string? Model, long Context, long Limit, double Cost, string Effort);

    private sealed record ScanResult(
        DateTime Today,
        double DayCost, long DayIn, long DayOut, long DayCache,
        List<(string Model, double Cost)> ByModelToday,
        DateTime? SessionWindowStartUtc, double SessionCost, long SessionIn, long SessionOut, long SessionCache,
        double Cost7, double Cost30, double CostAll,
        long InAll, long OutAll, long CacheAll,
        List<DailyPoint> DailySeries,
        List<ProjectTotals> ProjectTotals7d,
        List<(string Model, double Cost)> ModelByCost7d,
        List<SessionRow> RecentSessions,
        List<TabMetrics> TabMetrics);


    private static string ProjectLabelFromDir(string dir)
    {
        var name = Path.GetFileName(dir).TrimEnd('-');
        // Best-effort decoding of "C--Development-Foo-Bar" → "Foo\Bar". Just take the last segment for display.
        var parts = name.Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : name;
    }

    private static List<TerminalTabViewModel>? MatchProjectDir(string dir, Dictionary<string, List<TerminalTabViewModel>> tabsByPath)
    {
        var dirName = Path.GetFileName(dir);
        foreach (var kv in tabsByPath)
        {
            // Claude sanitises the cwd into the dir name by replacing path separators,
            // ':' and '.' with '-'. e.g. C:\Development\DataByte\DataByte.SportsAnalyser
            // → C--Development-DataByte-DataByte-SportsAnalyser.
            var encoded = kv.Key.Replace('\\', '-').Replace('/', '-').Replace(':', '-').Replace('.', '-');
            // Trim trailing separators so "C:\foo\" and "C:\foo" both match.
            encoded = encoded.TrimEnd('-');
            var dirTrim = dirName.TrimEnd('-');
            if (string.Equals(dirTrim, encoded, StringComparison.OrdinalIgnoreCase)
                || dirTrim.IndexOf(encoded, StringComparison.OrdinalIgnoreCase) >= 0
                || encoded.IndexOf(dirTrim, StringComparison.OrdinalIgnoreCase) >= 0)
                return kv.Value;
        }
        return null;
    }

    /// <summary>
    /// Pulls thinking-budget / thinking-block presence out of a transcript line. Claude Code stores
    /// the request body for assistant turns; <c>thinking.budget_tokens</c> tells us the configured
    /// effort, and any <c>type:"thinking"</c> content block confirms thinking actually fired.
    /// </summary>
    private static bool TryReadEffort(string line, out long budgetTokens, out bool hadThinking)
    {
        budgetTokens = 0;
        hadThinking = false;
        try
        {
            using var doc = JsonDocument.Parse(line, JsonOpts);
            var root = doc.RootElement;
            // thinking.budget_tokens may sit at the root or inside the message envelope
            if (TryReadBudget(root, out var b)) budgetTokens = b;
            if (root.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object)
            {
                if (budgetTokens == 0 && TryReadBudget(msg, out b)) budgetTokens = b;
                if (msg.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in content.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.Object) continue;
                        if (item.TryGetProperty("type", out var t)
                            && t.ValueKind == JsonValueKind.String
                            && string.Equals(t.GetString(), "thinking", StringComparison.OrdinalIgnoreCase))
                        {
                            hadThinking = true;
                            break;
                        }
                    }
                }
            }
            return budgetTokens > 0 || hadThinking;
        }
        catch { return false; }
    }

    private static bool TryReadBudget(JsonElement el, out long budget)
    {
        budget = 0;
        if (!el.TryGetProperty("thinking", out var th) || th.ValueKind != JsonValueKind.Object) return false;
        if (!th.TryGetProperty("budget_tokens", out var bt)) return false;
        if (bt.ValueKind != JsonValueKind.Number || !bt.TryGetInt64(out budget)) return false;
        return budget > 0;
    }

    /// <summary>
    /// Maps a thinking budget to Claude Code's effort tier names. Falls back to "thinking" when
    /// we only know thinking fired, or empty when the session is not using thinking at all.
    /// </summary>
    // Maps thinking budget_tokens to Claude Code's /effort tier names.
    // Thresholds chosen to match CC's defaults: low=1024, medium=4096, high=12000,
    // xhigh=31999, max=63999 (then a model-specific ceiling above that).
    private static string EffortLabel(long budget, bool hadThinking)
    {
        if (budget >= 63_999) return "max";
        if (budget >= 31_999) return "xhigh";
        if (budget >= 12_000) return "high";
        if (budget >= 4_000) return "medium";
        if (budget > 0) return "low";
        return string.Empty;
    }

    private static bool TryReadUsage(string line, out (long input, long output, long cacheRead, long cacheWrite) usage, out string? model, out DateTime? timestampUtc)
    {
        usage = default;
        model = null;
        timestampUtc = null;
        try
        {
            using var doc = JsonDocument.Parse(line, JsonOpts);
            var root = doc.RootElement;
            // Top-level "timestamp" sits next to "message" on assistant/user lines.
            if (root.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String
                && DateTime.TryParse(ts.GetString(), null,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var parsed))
            {
                timestampUtc = parsed;
            }
            JsonElement msg;
            if (root.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.Object)
                msg = m;
            else
                msg = root;
            if (msg.TryGetProperty("model", out var mod) && mod.ValueKind == JsonValueKind.String)
                model = mod.GetString();
            if (!msg.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object)
                return false;
            usage = (
                ReadLong(u, "input_tokens"),
                ReadLong(u, "output_tokens"),
                ReadLong(u, "cache_read_input_tokens"),
                ReadLong(u, "cache_creation_input_tokens"));
            return true;
        }
        catch { return false; }
    }

    private static long ReadLong(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l : 0;
    }

    private static double CostFor(string? model, long input, long output, long cacheRead, long cacheWrite)
    {
        if (model is null) return 0;
        var key = NormalizeModel(model);
        if (!Pricing.TryGetValue(key, out var p) && !TryFamilyPricing(key, out p)) return 0;
        return (input * p.input + output * p.output + cacheRead * p.cacheRead + cacheWrite * p.cacheWrite) / 1_000_000.0;
    }

    /// <summary>
    /// Fallback for model ids newer than the table above: price by family so a new
    /// release degrades to "approximately right" instead of zeroing every cost figure
    /// on the dashboard (which is what happened when claude-fable-5 shipped).
    /// </summary>
    private static bool TryFamilyPricing(string key, out (double input, double output, double cacheRead, double cacheWrite) p)
    {
        if (key.Contains("fable") || key.Contains("mythos")) { p = (10, 50, 1.0, 12.5); return true; }
        if (key.Contains("opus")) { p = (5, 25, 0.5, 6.25); return true; }
        if (key.Contains("sonnet")) { p = (3, 15, 0.3, 3.75); return true; }
        if (key.Contains("haiku")) { p = (1, 5, 0.1, 1.25); return true; }
        p = default;
        return false;
    }

    private static string NormalizeModel(string model)
    {
        // Strip date suffixes (-20251001) and feature suffixes ([1m]) — keep family.
        var s = model.ToLowerInvariant();
        var bracket = s.IndexOf('[');
        if (bracket >= 0) s = s[..bracket];
        for (int i = s.Length - 1; i >= 0 && (char.IsDigit(s[i]) || s[i] == '-'); i--)
        {
            // Trim trailing -YYYYMMDD if present
            if (s.Length - i >= 9 && s[i] == '-' && s.Substring(i + 1).All(char.IsDigit))
            {
                s = s[..i];
                break;
            }
        }
        return s;
    }

    private static string? ClaudeProjectsRoot()
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE")
                   ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(home)) return null;
        return Path.Combine(home, ".claude", "projects");
    }
}
