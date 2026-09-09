using Microsoft.Extensions.Logging;

namespace OmniFetch.Core.Logging;

/// <summary>
/// Configuration for the OmniFetch file logging subsystem.
/// In Development mode (DEBUG), all levels (Debug, Info, Warn, Error, Critical) are captured.
/// In Production mode (RELEASE), general logs are disabled, but Errors and Critical failures
/// are always enabled and flushed to disk immediately.
/// </summary>
public sealed class OmniFetchLogConfig
{
#if DEBUG
    public LogLevel MinimumLevel { get; set; } = LogLevel.Debug;
    public bool IsDevelopmentMode { get; set; } = true;
#else
    public LogLevel MinimumLevel { get; set; } = LogLevel.Error;
    public bool IsDevelopmentMode { get; set; } = false;
#endif

    public string LogDirectory { get; set; } = ResolveDefaultLogDirectory();
    public string LogFileName { get; set; } = "omnifetch.log";
    public long MaxFileSizeBytes { get; set; } = 10 * 1024 * 1024; // 10 MB
    public int MaxArchiveFiles { get; set; } = 3;

    public string FullLogPath => Path.Combine(LogDirectory, LogFileName);

    public static string ResolveDefaultLogDirectory()
    {
        string? homeDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(homeDir) && Directory.Exists(Path.Combine(homeDir, "Library")))
        {
            // macOS standard application logging path
            return Path.Combine(homeDir, "Library", "Logs", "OmniFetch");
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmniFetch",
            "Logs");
    }
}
