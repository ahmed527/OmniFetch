using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace OmniFetch.Core.Media;

public interface IFFmpegLocator
{
    string? FindFFmpeg();
    bool IsAvailable();
    Task<string?> GetVersionAsync(CancellationToken ct = default);
}

public class FFmpegLocator : IFFmpegLocator
{
    private static readonly string[] CandidatePaths =
    [
        "/opt/homebrew/bin/ffmpeg",       // Apple Silicon standard Homebrew
        "/usr/local/bin/ffmpeg",          // Intel Homebrew / legacy
        "/usr/bin/ffmpeg",                // System binary if present
        Path.Combine(AppContext.BaseDirectory, "ffmpeg"), // Alongside binary
        Path.Combine(AppContext.BaseDirectory, "..", "Resources", "ffmpeg") // App bundle Resources
    ];

    private string? _cachedPath;
    private bool _checked;

    public string? FindFFmpeg()
    {
        if (_checked && _cachedPath != null && File.Exists(_cachedPath))
        {
            return _cachedPath;
        }

        // 1. Check custom environment override
        string? envPath = Environment.GetEnvironmentVariable("OMNIFETCH_FFMPEG_PATH");
        if (!string.IsNullOrWhiteSpace(envPath) && File.Exists(envPath))
        {
            _cachedPath = envPath;
            _checked = true;
            return _cachedPath;
        }

        // 2. Check known macOS candidate paths
        foreach (var path in CandidatePaths)
        {
            try
            {
                if (File.Exists(path))
                {
                    _cachedPath = Path.GetFullPath(path);
                    _checked = true;
                    return _cachedPath;
                }
            }
            catch
            {
                // Ignored
            }
        }

        // 3. Fallback to `which ffmpeg`
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "which",
                    Arguments = "ffmpeg",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            if (proc.Start())
            {
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(1000);
                if (proc.ExitCode == 0 && File.Exists(output))
                {
                    _cachedPath = output;
                    _checked = true;
                    return _cachedPath;
                }
            }
        }
        catch
        {
            // Ignored
        }

        _checked = true;
        return null;
    }

    public bool IsAvailable() => FindFFmpeg() != null;

    public async Task<string?> GetVersionAsync(CancellationToken ct = default)
    {
        string? path = FindFFmpeg();
        if (path == null) return null;

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "-version",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!proc.Start()) return null;

            string output = await proc.StandardOutput.ReadLineAsync(ct).ConfigureAwait(false) ?? string.Empty;
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            return output;
        }
        catch
        {
            return null;
        }
    }
}
