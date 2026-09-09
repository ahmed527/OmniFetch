using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace OmniFetch.Bridge;

public static class Program
{
    public const string Version = "1.0.0";

    public static async Task<int> Main(string[] args)
    {
        if (args.Length > 0)
        {
            string cmd = args[0].ToLowerInvariant();
            switch (cmd)
            {
                case "--register":
                case "-r":
                    string? extId = args.Length > 1 ? args[1] : null;
                    return ManifestRegistrar.Register(null, extId);

                case "--unregister":
                case "-u":
                    return ManifestRegistrar.Unregister();

                case "--ping":
                case "-p":
                    return await PingDaemonAsync();

                case "--version":
                case "-v":
                    Console.WriteLine($"OmniFetch.Bridge version {Version} (Apple Silicon / Native AOT)");
                    return 0;

                case "--help":
                case "-h":
                    PrintHelp();
                    return 0;

                default:
                    Console.Error.WriteLine($"Unknown option: {args[0]}");
                    PrintHelp();
                    return 1;
            }
        }

        // Standard Chrome Native Messaging mode over stdio
        return await RunNativeMessagingHostAsync();
    }

    private static async Task<int> RunNativeMessagingHostAsync()
    {
        using var stdin = Console.OpenStandardInput();
        using var stdout = Console.OpenStandardOutput();
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        try
        {
            while (!cts.IsCancellationRequested)
            {
                byte[]? messagePayload = await NativeProtocol.ReadMessageAsync(stdin, cts.Token);
                if (messagePayload == null)
                {
                    // Browser closed the input pipe (session complete)
                    break;
                }

                byte[] responseBytes = await UnixSocketRelay.RelayAsync(messagePayload, null, cts.Token);
                await NativeProtocol.WriteMessageAsync(stdout, responseBytes, cts.Token);
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            return 0;
        }
        catch (Exception ex)
        {
            var err = new BridgeResponse
            {
                Status = "error",
                ErrorCode = "BRIDGE_INTERNAL_ERROR",
                Message = ex.Message
            };
            try
            {
                await NativeProtocol.WriteResponseAsync(stdout, err, CancellationToken.None);
            }
            catch
            {
                // Suppress if stdout broken
            }
            return 1;
        }
    }

    private static async Task<int> PingDaemonAsync()
    {
        string pingPayload = "{\"action\":\"ping\",\"client\":\"OmniFetch.Bridge CLI\"}";
        byte[] payloadBytes = System.Text.Encoding.UTF8.GetBytes(pingPayload);
        Console.WriteLine($"Pinging OmniFetch daemon at {UnixSocketRelay.GetDefaultSocketPath()}...");

        byte[] responseBytes = await UnixSocketRelay.RelayAsync(payloadBytes);
        string responseJson = System.Text.Encoding.UTF8.GetString(responseBytes);
        Console.WriteLine($"Response: {responseJson}");
        return 0;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("OmniFetch Native Messaging IPC Bridge");
        Console.WriteLine("Usage:");
        Console.WriteLine("  OmniFetch.Bridge                        Run as Chrome Native Messaging Host (stdio)");
        Console.WriteLine("  OmniFetch.Bridge --register [ext-id]    Register host manifest for Chrome, Brave, Edge");
        Console.WriteLine("  OmniFetch.Bridge --unregister           Unregister host manifests");
        Console.WriteLine("  OmniFetch.Bridge --ping                 Test connection to ~/.omnifetch.sock");
        Console.WriteLine("  OmniFetch.Bridge --version              Print version information");
        Console.WriteLine("  OmniFetch.Bridge --help                 Display this help message");
    }
}
