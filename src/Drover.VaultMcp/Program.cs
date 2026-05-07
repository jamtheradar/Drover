using System;
using System.Text;

namespace Drover.VaultMcp;

internal static class Program
{
    // Entry point. Parses --project <abs path>, then runs the JSON-RPC
    // read loop. Output is forced to UTF-8 without BOM so Windows
    // consoles (and the Claude Code parser) read clean ASCII bytes for
    // each protocol frame.
    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        string? projectRoot = null;
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--project" && i + 1 < args.Length)
            {
                projectRoot = args[++i];
            }
        }

        try
        {
            var server = new JsonRpcServer(projectRoot, Console.In, Console.Out);
            return server.Run();
        }
        catch (Exception ex)
        {
            Logging.Error("Top-level fatal exception.", ex);
            return 1;
        }
    }
}
