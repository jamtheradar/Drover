using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Drover.App.Services;

/// <summary>
/// Per-tab clean-transcript writer. Drives off the same hook stream that powers attention
/// state — UserPromptSubmit / PostToolUse / Stop fire at clean boundaries — and reads CC's
/// own JSONL transcript (<c>transcript_path</c> in every hook envelope) to extract the
/// user→assistant message pairs without trying to reconstruct anything from the VT stream.
///
/// Output is a Markdown file alongside the raw <c>.log</c> (same stamp prefix). Tool calls
/// are summarised as a one-liner; tool results and thinking blocks are dropped — this file
/// is for reading, sharing, or feeding back into spec creation, not faithful replay.
///
/// Dedup is by message UUID and a byte offset into the JSONL: hook events fire frequently
/// (every PostToolUse) so we tail the file incrementally rather than re-scanning each time.
/// All state mutation goes through <see cref="_lock"/> because the hooks gateway calls
/// <see cref="OnHookEvent"/> from a background thread.
/// </summary>
public sealed class TranscriptCapture : IDisposable
{
    /// <summary>CSI / OSC / etc. — defensive; CC's JSONL message text is normally clean.</summary>
    private static readonly Regex AnsiEscape = new(
        @"\x1b\[[\x30-\x3f]*[\x20-\x2f]*[\x40-\x7e]|\x1b[\]PX^_][\s\S]*?(?:\x07|\x1b\\)|\x1b[\x20-\x2f]*[\x30-\x7e]",
        RegexOptions.Compiled);

    /// <summary>UTF-8 without BOM. The raw .log may carry a BOM from terminal capture; this file shouldn't.</summary>
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly object _lock = new();
    private readonly HashSet<string> _writtenUuids = new(StringComparer.Ordinal);
    private StreamWriter? _writer;
    private string? _ccTranscriptPath;
    private long _ccByteOffset;

    public string Path { get; }
    public string? AgentDisplayName { get; }
    public string? Model { get; }

    public TranscriptCapture(string path, string? agentDisplayName = null, string? model = null)
    {
        Path = path;
        AgentDisplayName = agentDisplayName;
        Model = model;
    }

    public void Attach()
    {
        try
        {
            _writer = new StreamWriter(Path, append: false, Utf8NoBom) { AutoFlush = false };
            var agentLabel = string.IsNullOrWhiteSpace(AgentDisplayName) ? "—" : AgentDisplayName!;
            var modelLabel = string.IsNullOrWhiteSpace(Model) ? "—" : Model!;
            _writer.WriteLine($"<!-- Drover transcript — agent: {agentLabel} — model: {modelLabel} — started {DateTime.Now:O} -->");
            _writer.WriteLine();
            _writer.Flush();
        }
        catch (Exception ex)
        {
            AppLog.Warn("TranscriptCapture", $"failed to open {Path}: {ex.Message}");
            _writer = null;
        }
    }

    public void OnHookEvent(HookEvent evt)
    {
        if (_writer is null) return;
        // Three boundary events. UserPromptSubmit and PostToolUse let us stream pairs
        // as the turn progresses; Stop is the catch-all flush at end of turn.
        if (evt.Type is not ("UserPromptSubmit" or "PostToolUse" or "Stop")) return;

        var path = ReadTranscriptPath(evt.RawBody);
        if (string.IsNullOrEmpty(path)) return;

        lock (_lock)
        {
            // Switching transcript path resets the offset — happens on /resume or
            // when CC starts a new conversation in the same tab.
            if (!string.Equals(_ccTranscriptPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _ccTranscriptPath = path;
                _ccByteOffset = 0;
                _writtenUuids.Clear();
            }
            DrainLocked();
        }
    }

    public void Flush()
    {
        lock (_lock)
        {
            DrainLocked();
            try { _writer?.Flush(); } catch { }
        }
    }

    private void DrainLocked()
    {
        if (_writer is null || _ccTranscriptPath is null) return;
        if (!File.Exists(_ccTranscriptPath)) return;
        try
        {
            // FileShare.ReadWrite — CC keeps the JSONL open for append while the
            // session is live, so the default share=Read would throw.
            using var fs = new FileStream(_ccTranscriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (_ccByteOffset > fs.Length) _ccByteOffset = 0; // file truncated/rotated
            fs.Seek(_ccByteOffset, SeekOrigin.Begin);
            using var sr = new StreamReader(fs, Encoding.UTF8);
            string? line;
            while ((line = sr.ReadLine()) != null)
            {
                AppendLine(line);
            }
            _ccByteOffset = fs.Position;
            _writer.Flush();
        }
        catch (Exception ex)
        {
            // Read failures are non-fatal — next hook event retries from the same offset.
            AppLog.Warn("TranscriptCapture", $"drain failed for {_ccTranscriptPath}: {ex.Message}");
        }
    }

    private void AppendLine(string jsonl)
    {
        if (string.IsNullOrWhiteSpace(jsonl)) return;
        try
        {
            using var doc = JsonDocument.Parse(jsonl);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return;

            // Dedup. CC writes in append-only fashion so the same uuid only re-appears
            // if a previous run's file is still on disk; cheap insurance.
            string? uuid = null;
            if (root.TryGetProperty("uuid", out var u) && u.ValueKind == JsonValueKind.String)
            {
                uuid = u.GetString();
                if (uuid is not null && !_writtenUuids.Add(uuid)) return;
            }

            string? type = null;
            if (root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                type = t.GetString();
            if (type != "user" && type != "assistant") return;

            if (!root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object) return;

            var content = ExtractContent(message);
            // Empty user messages are silently skipped — they're typically tool_result
            // wrappers with no human text to surface. An empty assistant message means
            // an interrupted turn; record the placeholder rather than dropping it so
            // a reader can see *something* happened.
            if (string.IsNullOrWhiteSpace(content))
            {
                if (type != "assistant") return;
                content = "_(turn interrupted)_";
            }

            var heading = type == "user" ? "## User" : "## Assistant";
            _writer!.WriteLine(heading);
            _writer.WriteLine();
            _writer.WriteLine(content.TrimEnd());
            _writer.WriteLine();
        }
        catch
        {
            // Malformed line — skip. The JSONL might be mid-write.
        }
    }

    private static string ExtractContent(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return string.Empty;

        if (content.ValueKind == JsonValueKind.String)
            return StripAnsi(content.GetString() ?? string.Empty);

        if (content.ValueKind != JsonValueKind.Array) return string.Empty;

        // Spec §3.11: tools collapse to ONE italic line at the top of the assistant
        // section, then the final text. Per-block interleaving (the previous shape)
        // makes a turn with N tools harder to scan, and the order doesn't carry
        // meaning anyone reading the transcript cares about.
        var tools = new List<string>();
        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind != JsonValueKind.Object) continue;
            string? btype = block.TryGetProperty("type", out var bt) && bt.ValueKind == JsonValueKind.String
                ? bt.GetString()
                : null;

            if (btype == "text" && block.TryGetProperty("text", out var txt) && txt.ValueKind == JsonValueKind.String)
            {
                var s = StripAnsi(txt.GetString() ?? string.Empty);
                if (!string.IsNullOrWhiteSpace(s))
                {
                    if (text.Length > 0) text.AppendLine();
                    text.AppendLine(s);
                }
            }
            else if (btype == "tool_use")
            {
                var name = block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    ? n.GetString()
                    : "tool";
                if (!string.IsNullOrEmpty(name)) tools.Add(name!);
            }
            // tool_result / thinking blocks are intentionally dropped — this transcript
            // is for human reading, not replay. The raw .log (Full level) has them.
        }

        var sb = new StringBuilder();
        if (tools.Count > 0)
        {
            sb.Append("_(used ");
            for (int i = 0; i < tools.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append('`').Append(tools[i]).Append('`');
            }
            sb.AppendLine(")_");
            sb.AppendLine();
        }
        if (text.Length > 0) sb.Append(text);
        return sb.ToString();
    }

    private static string ReadTranscriptPath(string rawBody)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return string.Empty;
            if (doc.RootElement.TryGetProperty("transcript_path", out var tp) && tp.ValueKind == JsonValueKind.String)
                return tp.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }

    private static string StripAnsi(string s)
        => string.IsNullOrEmpty(s) ? s : AnsiEscape.Replace(s, string.Empty);

    public void Dispose()
    {
        lock (_lock)
        {
            try { _writer?.Flush(); } catch { }
            try { _writer?.Dispose(); } catch { }
            _writer = null;
        }
    }
}
