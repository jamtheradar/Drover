using System.Net.Sockets;
using System.Text;

// Drover hook forwarder. Claude Code pipes the hook (or statusLine) JSON to stdin;
// this POSTs it to Drover's loopback gateway tagged with the tab's session id.
//
//   drover-hook.exe               hook event   → X-Drover-Session
//   drover-hook.exe --statusline  statusLine   → also X-Drover-Kind: statusline, and
//                                               prints an empty line (CC renders stdout)
//
// Silent and best-effort, always exit 0: a missing or hung Drover must never block or
// fail a Claude Code turn. Outside Drover (env vars unset) it exits before reading
// stdin or touching the network. Raw HTTP/1.1 over a socket rather than HttpClient —
// this runs on every tool call and HttpClient's load cost dominates start-up.

var statusLine = args.Contains("--statusline");
try
{
    var url = Environment.GetEnvironmentVariable("DROVER_HOOKS_URL");
    var sid = Environment.GetEnvironmentVariable("DROVER_SESSION_ID");
    if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(sid)
        || !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "http")
        return Finish();

    using var stdin = Console.OpenStandardInput();
    using var buffer = new MemoryStream();
    stdin.CopyTo(buffer);
    if (buffer.Length == 0) return Finish();

    var header = new StringBuilder()
        .Append("POST ").Append(uri.PathAndQuery).Append(" HTTP/1.1\r\n")
        .Append("Host: ").Append(uri.Authority).Append("\r\n")
        .Append("Content-Type: application/json\r\n")
        .Append("X-Drover-Session: ").Append(sid).Append("\r\n");
    if (statusLine) header.Append("X-Drover-Kind: statusline\r\n");
    header.Append("Content-Length: ").Append(buffer.Length).Append("\r\n")
          .Append("Connection: close\r\n\r\n");

    using var client = new TcpClient { SendTimeout = 2000, ReceiveTimeout = 2000, NoDelay = true };
    // Loopback connects take <1 ms; a refused port makes Windows retry SYNs for ~1 s,
    // so cap the wait well below that.
    if (!client.ConnectAsync(uri.Host, uri.Port).Wait(300)) return Finish();
    using var net = client.GetStream();
    var headBytes = Encoding.ASCII.GetBytes(header.ToString());
    net.Write(headBytes, 0, headBytes.Length);
    buffer.Position = 0;
    buffer.CopyTo(net);
    net.Flush();

    // Wait for the status line so the gateway has the event before CC moves on
    // (keeps events ordered); the body is irrelevant.
    var resp = new byte[64];
    _ = net.Read(resp, 0, resp.Length);
}
catch
{
    // Best effort — swallow everything.
}
return Finish();

int Finish()
{
    if (statusLine)
    {
        try { Console.Out.WriteLine(); } catch { }
    }
    return 0;
}
