using System;
using System.Collections.Generic;
using System.IO;

namespace OmniFetch.Core.Settings;

/// <summary>
/// Global application settings and per-category destination folders.
/// </summary>
public sealed class OmniFetchSettings
{
    /// <summary>
    /// Default root download directory (e.g. ~/Downloads).
    /// </summary>
    public string DefaultDownloadDirectory { get; set; } = 
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    /// <summary>
    /// Specific download directories mapped by category ("Video", "Music", "Compressed", "Documents", "Programs", "General").
    /// </summary>
    public Dictionary<string, string> CategoryDirectories { get; set; } = new(StringComparer.OrdinalIgnoreCase)
    {
        ["General"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        ["Video"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Video"),
        ["Music"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Music"),
        ["Compressed"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Compressed"),
        ["Documents"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Documents"),
        ["Programs"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Programs")
    };

    /// <summary>
    /// Updates the default download directory and immediately updates all default category subdirectories.
    /// </summary>
    public void UpdateDefaultDownloadDirectory(string newDefault)
    {
        if (string.IsNullOrWhiteSpace(newDefault)) return;

        DefaultDownloadDirectory = newDefault;
        CategoryDirectories["General"] = newDefault;
        CategoryDirectories["Video"] = Path.Combine(newDefault, "Video");
        CategoryDirectories["Music"] = Path.Combine(newDefault, "Music");
        CategoryDirectories["Compressed"] = Path.Combine(newDefault, "Compressed");
        CategoryDirectories["Documents"] = Path.Combine(newDefault, "Documents");
        CategoryDirectories["Programs"] = Path.Combine(newDefault, "Programs");
    }

    /// <summary>
    /// Whether to show the "Download File Info" confirmation dialog before downloading.
    /// Default: true (matching IDM).
    /// </summary>
    public bool ShowStartDialog { get; set; } = true;

    /// <summary>
    /// Whether to show the Download Complete notification dialog.
    /// Default: true.
    /// </summary>
    public bool ShowCompleteDialog { get; set; } = true;

    /// <summary>
    /// Whether to automatically close the download progress dialog when the download completes.
    /// Default: true.
    /// </summary>
    public bool AutoCloseProgressDialogOnCompletion { get; set; } = true;

    /// <summary>
    /// Maximum parallel connections per download task (1 to 32, default 8).
    /// </summary>
    public int MaxConnections { get; set; } = 8;

    /// <summary>
    /// Whether global speed limiter is active.
    /// </summary>
    public bool SpeedLimitEnabled { get; set; } = false;

    /// <summary>
    /// Global speed limit in KB/s.
    /// </summary>
    public double SpeedLimitKbps { get; set; } = 1024.0;
}
