using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace OmniFetch.Core.Logging;

/// <summary>
/// Thread-safe file logger for OmniFetch.
/// Automatically handles rotation, flush-on-error, and development vs production level filtering.
/// </summary>
public sealed class OmniFetchFileLogger : ILogger
{
    private readonly string _categoryName;
    private readonly OmniFetchLogConfig _config;
    private static readonly object _syncLock = new();

    public OmniFetchFileLogger(string categoryName, OmniFetchLogConfig config)
    {
        _categoryName = categoryName ?? "OmniFetch";
        _config = config ?? new OmniFetchLogConfig();
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel)
    {
        // Requirement: Errors and Critical failures MUST ALWAYS be enabled, even in Release mode.
        if (logLevel >= LogLevel.Error)
        {
            return true;
        }

        return logLevel >= _config.MinimumLevel;
    }

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        if (formatter == null)
        {
            return;
        }

        string message = formatter(state, exception);
        if (string.IsNullOrEmpty(message) && exception == null)
        {
            return;
        }

        string timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        string levelStr = logLevel switch
        {
            LogLevel.Trace => "TRACE",
            LogLevel.Debug => "DEBUG",
            LogLevel.Information => "INFO ",
            LogLevel.Warning => "WARN ",
            LogLevel.Error => "ERROR",
            LogLevel.Critical => "CRIT ",
            _ => "NONE "
        };

        var sb = new StringBuilder(256);
        sb.Append('[').Append(timestamp).Append(" UTC] [")
          .Append(levelStr).Append("] [")
          .Append(_categoryName).Append("] ")
          .Append(message);

        if (exception != null)
        {
            sb.AppendLine();
            sb.Append("Exception: ").Append(exception.ToString());
        }

        string formattedEntry = sb.ToString();

        try
        {
            lock (_syncLock)
            {
                EnsureDirectoryExists(_config.LogDirectory);
                RotateLogFileIfNeeded(_config);

                using var stream = new FileStream(
                    _config.FullLogPath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);

                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.WriteLine(formattedEntry);

                // Always flush immediately on errors to ensure zero crash telemetry loss
                if (logLevel >= LogLevel.Error)
                {
                    writer.Flush();
                    stream.Flush(flushToDisk: true);
                }
            }
        }
        catch
        {
            // Logging must never throw or crash the application
        }
    }

    private static void EnsureDirectoryExists(string dir)
    {
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
    }

    private static void RotateLogFileIfNeeded(OmniFetchLogConfig config)
    {
        try
        {
            var fileInfo = new FileInfo(config.FullLogPath);
            if (!fileInfo.Exists || fileInfo.Length < config.MaxFileSizeBytes)
            {
                return;
            }

            for (int i = config.MaxArchiveFiles - 1; i >= 1; i--)
            {
                string src = $"{config.FullLogPath}.{i}";
                string dst = $"{config.FullLogPath}.{i + 1}";
                if (File.Exists(src))
                {
                    File.Move(src, dst, overwrite: true);
                }
            }

            string firstArchive = $"{config.FullLogPath}.1";
            File.Move(config.FullLogPath, firstArchive, overwrite: true);
        }
        catch
        {
            // Ignore rotation errors
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();
        public void Dispose() { }
    }
}
