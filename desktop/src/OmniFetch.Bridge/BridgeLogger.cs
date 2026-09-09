using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace OmniFetch.Bridge;

/// <summary>
/// Native AOT compatible file logger for the OmniFetch Native Messaging Bridge.
/// STRICT REQUIREMENT: This logger NEVER writes to stdout (Console.Out), as any unformatted
/// bytes sent to stdout corrupt the Chrome Native Messaging protocol and crash the connection.
/// In Release mode, this logger captures only Errors and Critical failures.
/// In Debug mode, it captures Debug, Info, Warning, and Errors.
/// </summary>
public static class BridgeLogger
{
    private static readonly object _syncLock = new();
    private static readonly string _logDirectory;
    private static readonly string _logFilePath;

    static BridgeLogger()
    {
        string homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(homeDir) && Directory.Exists(Path.Combine(homeDir, "Library")))
        {
            _logDirectory = Path.Combine(homeDir, "Library", "Logs", "OmniFetch");
        }
        else
        {
            _logDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmniFetch", "Logs");
        }

        _logFilePath = Path.Combine(_logDirectory, "omnifetch-bridge.log");
    }

    public static string LogFilePath => _logFilePath;

    public static void LogDebug(string message)
    {
#if DEBUG
        Write("DEBUG", message, null);
#endif
    }

    public static void LogInfo(string message)
    {
#if DEBUG
        Write("INFO ", message, null);
#endif
    }

    public static void LogWarning(string message)
    {
#if DEBUG
        Write("WARN ", message, null);
#endif
    }

    public static void LogError(string message, Exception? ex = null)
    {
        // Errors are ALWAYS logged, in both Debug and Release modes
        Write("ERROR", message, ex);
    }

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
            var sb = new StringBuilder(256);
            sb.Append('[').Append(timestamp).Append(" UTC] [")
              .Append(level).Append("] [OmniFetch.Bridge] ")
              .Append(message);

            if (ex != null)
            {
                sb.AppendLine();
                sb.Append("Exception: ").Append(ex.ToString());
            }

            string entry = sb.ToString();

            lock (_syncLock)
            {
                if (!Directory.Exists(_logDirectory))
                {
                    Directory.CreateDirectory(_logDirectory);
                }

                using var stream = new FileStream(
                    _logFilePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);

                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.WriteLine(entry);
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }
        }
        catch
        {
            // Bridge must never crash due to logging failures
        }
    }
}
