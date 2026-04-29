using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows.Threading;
using Drover.App.ViewModels;

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

    // Best-effort pricing per 1M tokens (USD) for common Claude models. Updated 2026-04.
    // We fail-soft to zero if we don't recognise the model; cost is informational only.
    private static readonly Dictionary<string, (double input, double output, double cacheRead, double cacheWrite)> Pricing =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["claude-opus-4-7"] = (15, 75, 1.5, 18.75),
            ["claude-opus-4-6"] = (15, 75, 1.5, 18.75),
            ["claude-opus-4"] = (15, 75, 1.5, 18.75),
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
        _timer.Tick += (_, _) => Refresh();
    }

    public void Start()
    {
        Refresh();
        _timer.Start();
    }

    public void Refresh()
    {
        try { RefreshCore(); } catch { /* never propagate from a background poll */ }
    }

    private void RefreshCore()
    {
        var root = ClaudeProjectsRoot();
        if (root is null || !Directory.Exists(root)) return;

        var today = DateTime.UtcNow.Date;
        _lastDayKey = today;

        // Collect per-tab metrics by walking transcripts in each project dir.
        // Claude encodes the cwd in the dir name; we match loosely by the path
        // basename for resilience against the exact encoding scheme.
        var tabsByPath = _shell.Tabs
            .GroupBy(t => t.Project.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
        if (_shell.SecondaryTab is { } sec)
        {
            if (!tabsByPath.TryGetValue(sec.Project.Path, out var list))
                tabsByPath[sec.Project.Path] = list = new();
            list.Add(sec);
        }

        double dayCost = 0;
        long dayIn = 0, dayOut = 0, dayCache = 0;
        var byModelToday = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        // Rolling 5h usage window — collect every usage line in the last 24h, then walk
        // them in time order after the file scan to find the first message of the *current*
        // window. Anthropic anchors each 5h window on its first message; "earliest in last
        // 5h" is wrong when a previous window straddles the boundary.
        var nowUtc = DateTime.UtcNow;
        var sessionScanEdge = nowUtc - TimeSpan.FromHours(24);
        var sessionEntries = new List<(DateTime ts, double cost, long input, long output, long cache)>(256);

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
        var sevenDaysAgo = today.AddDays(-6);
        var thirtyDaysAgo = today.AddDays(-29);

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

            foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
            {
                long inTok = 0, outTok = 0, cRead = 0, cWrite = 0;
                string? model = null;
                long lastInputForContext = 0;

                using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var sr = new StreamReader(fs);
                string? line;
                var fileWriteUtc = File.GetLastWriteTimeUtc(file);
                long fileBudget = 0;
                bool fileHasThinking = false;
                // Skip per-line timestamp capture entirely if the whole file is older than the scan edge.
                var maybeInScan = fileWriteUtc >= sessionScanEdge;
                while ((line = sr.ReadLine()) is not null)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    // Cheap upfront detection: a `"type":"thinking"` substring is a sufficient
                    // signal that a thinking block is present on this line. Avoids the cost of
                    // full JSON-parsing every assistant turn (transcript lines can be huge) and
                    // sidesteps any future schema drift around where budget_tokens lives.
                    if (line.IndexOf("\"type\":\"thinking\"", System.StringComparison.Ordinal) >= 0)
                        fileHasThinking = true;
                    // Budget often appears inside the request body which CC stores as an
                    // escaped JSON string, so the literal `"budget_tokens":N` is JSON-escaped to
                    // `\"budget_tokens\":N`. The regex tolerates both unescaped and escaped forms
                    // and keeps the largest budget seen on this line.
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
                    if (!TryReadUsage(line, out var u, out var m, out var ts)) continue;
                    if (m is not null) model = m;
                    inTok += u.input;
                    outTok += u.output;
                    cRead += u.cacheRead;
                    cWrite += u.cacheWrite;
                    lastInputForContext = u.input + u.cacheRead + u.cacheWrite;

                    if (maybeInScan && ts is { } tsv && tsv >= sessionScanEdge)
                    {
                        var entryCost = CostFor(m ?? model, u.input, u.output, u.cacheRead, u.cacheWrite);
                        sessionEntries.Add((tsv, entryCost, u.input, u.output, u.cacheRead + u.cacheWrite));
                    }
                }

                var fileCost = CostFor(model, inTok, outTok, cRead, cWrite);
                var fileTokens = inTok + outTok + cRead + cWrite;
                var fileDate = fileWriteUtc.Date;

                // All-time
                costAll += fileCost;
                inAll += inTok;
                outAll += outTok;
                cacheAll += cRead + cWrite;

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
                        Input = pt.Input + inTok,
                        Output = pt.Output + outTok,
                        Cache = pt.Cache + cRead + cWrite
                    };
                }

                // Today
                if (fileDate == today)
                {
                    dayCost += fileCost;
                    dayIn += inTok;
                    dayOut += outTok;
                    dayCache += cRead + cWrite;
                    var modelLabel = model ?? "unknown";
                    byModelToday.TryGetValue(modelLabel, out var prev);
                    byModelToday[modelLabel] = prev + fileCost;
                }

                // Recent sessions (any session with usage in the last 30 days)
                if (fileDate >= thirtyDaysAgo && fileTokens > 0)
                {
                    sessions.Add(new SessionRow(fileWriteUtc, projectLabel, model, fileCost, fileTokens));
                }

                if (match is not null)
                {
                    if (model is not null) fallbackModel = model;
                    if (fileWriteUtc > sessionMtime)
                    {
                        sessionMtime = fileWriteUtc;
                        sessionModel = model;
                        sessionContext = lastInputForContext;
                        sessionBudget = fileBudget;
                        sessionHadThinking = fileHasThinking;
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
                foreach (var tab in match)
                    tab.UpdateMetrics(modelOut, sessionContext, limit, sessionCost, effort);
            }
        }

        // Walk usage entries in time order to find the first message of the *current*
        // 5h window. A new window opens when the gap from the last accepted message
        // is >= 5h. The current window is whichever the most recent message falls into.
        sessionEntries.Sort((a, b) => a.ts.CompareTo(b.ts));
        DateTime? curStart = null;
        foreach (var (ts, _, _, _, _) in sessionEntries)
        {
            if (curStart is null || ts >= curStart.Value + SessionWindowLength)
                curStart = ts;
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
                if (e.ts < start) continue;
                winCost += e.cost;
                winIn += e.input;
                winOut += e.output;
                winCache += e.cache;
            }
        }
        SessionCostUsd = winCost;
        SessionInputTokens = winIn;
        SessionOutputTokens = winOut;
        SessionCacheTokens = winCache;
        SessionWindowStartUtc = curStart;
        SessionResetUtc = curStart is { } e2 ? e2 + SessionWindowLength : null;

        DailyCostUsd = dayCost;
        DailyInputTokens = dayIn;
        DailyOutputTokens = dayOut;
        DailyCacheTokens = dayCache;
        DailyByModel = byModelToday
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        Last7DaysCostUsd = cost7;
        Last30DaysCostUsd = cost30;
        AllTimeCostUsd = costAll;
        AllTimeInputTokens = inAll;
        AllTimeOutputTokens = outAll;
        AllTimeCacheTokens = cacheAll;
        DailySeries = dailyMap.Values.OrderBy(p => p.Date).ToList();
        ProjectTotals7d = byProject7
            .OrderByDescending(kv => kv.Value.cost)
            .Select(kv => new ProjectTotals(kv.Key, kv.Value.cost, kv.Value.tokens))
            .ToList();
        ModelByCost7d = byModel7
            .OrderByDescending(kv => kv.Value)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();
        RecentSessions = sessions
            .OrderByDescending(s => s.LastWriteUtc)
            .Take(20)
            .ToList();

        Updated?.Invoke(this, EventArgs.Empty);
    }

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
        if (budget >= 4_000)  return "medium";
        if (budget > 0)       return "low";
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
        if (!Pricing.TryGetValue(key, out var p)) return 0;
        return (input * p.input + output * p.output + cacheRead * p.cacheRead + cacheWrite * p.cacheWrite) / 1_000_000.0;
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
