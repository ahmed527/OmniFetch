using System;
using System.Collections.Generic;
using System.IO;

namespace OmniFetch.Core.Common;

/// <summary>
/// Provides fast, bidirectional mapping between MIME types and file extensions,
/// and intelligent filename sanitization to eliminate generic .dat/.bin extensions.
/// </summary>
public static class MimeTypeMap
{
    private static readonly Dictionary<string, string> MimeToExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        // Video
        ["video/mp4"] = ".mp4",
        ["video/webm"] = ".webm",
        ["video/x-matroska"] = ".mkv",
        ["video/quicktime"] = ".mov",
        ["video/x-msvideo"] = ".avi",
        ["video/x-flv"] = ".flv",
        ["video/mp2t"] = ".ts",
        ["video/3gpp"] = ".3gp",
        ["video/x-ms-wmv"] = ".wmv",

        // Audio
        ["audio/mp4"] = ".m4a",
        ["audio/mpeg"] = ".mp3",
        ["audio/webm"] = ".weba",
        ["audio/ogg"] = ".ogg",
        ["audio/wav"] = ".wav",
        ["audio/x-wav"] = ".wav",
        ["audio/flac"] = ".flac",
        ["audio/aac"] = ".aac",

        // Streaming Playlists
        ["application/x-mpegURL"] = ".mp4",
        ["application/vnd.apple.mpegurl"] = ".mp4",
        ["application/dash+xml"] = ".mp4",

        // Documents
        ["application/pdf"] = ".pdf",
        ["text/plain"] = ".txt",
        ["text/html"] = ".html",
        ["application/json"] = ".json",
        ["application/xml"] = ".xml",
        ["application/msword"] = ".doc",
        ["application/vnd.openxmlformats-officedocument.wordprocessingml.document"] = ".docx",
        ["application/vnd.ms-excel"] = ".xls",
        ["application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"] = ".xlsx",
        ["application/vnd.ms-powerpoint"] = ".ppt",
        ["application/vnd.openxmlformats-officedocument.presentationml.presentation"] = ".pptx",

        // Archives / Compressed
        ["application/zip"] = ".zip",
        ["application/x-zip-compressed"] = ".zip",
        ["application/x-rar-compressed"] = ".rar",
        ["application/x-7z-compressed"] = ".7z",
        ["application/x-tar"] = ".tar",
        ["application/gzip"] = ".gz",
        ["application/x-bzip2"] = ".bz2",
        ["application/x-xz"] = ".xz",

        // Images
        ["image/jpeg"] = ".jpg",
        ["image/png"] = ".png",
        ["image/gif"] = ".gif",
        ["image/webp"] = ".webp",
        ["image/svg+xml"] = ".svg",

        // Applications & Executables
        ["application/x-apple-diskimage"] = ".dmg",
        ["application/vnd.apple.installer+xml"] = ".pkg",
        ["application/x-msdownload"] = ".exe",
        ["application/x-iso9660-image"] = ".iso"
    };

    /// <summary>
    /// Returns the standard file extension for the specified MIME media type, or null if unknown.
    /// </summary>
    public static string? GetExtension(string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(mimeType)) return null;

        // Strip charset or parameters, e.g. "video/mp4; codecs=..."
        int semicolon = mimeType.IndexOf(';');
        string cleanMime = (semicolon >= 0 ? mimeType[..semicolon] : mimeType).Trim();

        return MimeToExtension.TryGetValue(cleanMime, out var ext) ? ext : null;
    }

    /// <summary>
    /// Sniffs MIME type from URL query parameters (e.g. YouTube googlevideo.com/videoplayback?mime=video%2Fmp4).
    /// </summary>
    public static string? SniffMimeFromUrl(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;

        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                var query = uri.Query;
                if (!string.IsNullOrEmpty(query))
                {
                    var pairs = query.TrimStart('?').Split('&');
                    foreach (var pair in pairs)
                    {
                        var kv = pair.Split('=');
                        if (kv.Length == 2 && string.Equals(kv[0], "mime", StringComparison.OrdinalIgnoreCase))
                        {
                            return Uri.UnescapeDataString(kv[1]);
                        }
                    }
                }
            }
        }
        catch
        {
            // Ignore parse errors
        }

        return null;
    }

    /// <summary>
    /// Sanitizes a file name, eliminates dummy extensions (.dat, .bin, .tmp), and guarantees
    /// the accurate extension based on Content-Type, URL parameters, or YouTube stream characteristics.
    /// </summary>
    public static string SanitizeAndEnsureExtension(string? rawFileName, string? contentType, string? url)
    {
        string name = string.IsNullOrWhiteSpace(rawFileName) ? "download" : rawFileName.Trim();

        // 1. Remove illegal characters for file systems
        var invalidChars = Path.GetInvalidFileNameChars();
        name = string.Concat(name.Split(invalidChars));
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "download";
        }

        // 2. Identify current extension
        string existingExt = Path.GetExtension(name).ToLowerInvariant();
        bool isDummyExtension = string.IsNullOrEmpty(existingExt) ||
                                existingExt is ".dat" or ".bin" or ".tmp" or ".crdownload" or ".part";

        // 3. Resolve preferred extension from Content-Type or URL query params
        string? preferredExt = GetExtension(contentType);

        if (string.IsNullOrEmpty(preferredExt) && !string.IsNullOrWhiteSpace(url))
        {
            string? urlMime = SniffMimeFromUrl(url);
            if (!string.IsNullOrEmpty(urlMime))
            {
                preferredExt = GetExtension(urlMime);
            }
        }

        // 4. Special handling for YouTube / googlevideo streams
        bool isYouTube = !string.IsNullOrWhiteSpace(url) &&
                         (url.Contains("googlevideo.com", StringComparison.OrdinalIgnoreCase) ||
                          url.Contains("videoplayback", StringComparison.OrdinalIgnoreCase));

        if (isYouTube && string.IsNullOrEmpty(preferredExt))
        {
            preferredExt = ".mp4"; // Default progressive video container
        }

        // 5. Replace dummy extension or append preferred extension
        if (isDummyExtension && !string.IsNullOrEmpty(preferredExt))
        {
            name = Path.ChangeExtension(name, preferredExt);
        }
        else if (isYouTube && existingExt is ".dat" or ".bin")
        {
            name = Path.ChangeExtension(name, preferredExt ?? ".mp4");
        }

        // 6. Clean up generic 'videoplayback' base names
        if (string.Equals(Path.GetFileNameWithoutExtension(name), "videoplayback", StringComparison.OrdinalIgnoreCase))
        {
            string ext = Path.GetExtension(name);
            if (string.IsNullOrEmpty(ext)) ext = ".mp4";
            name = "YouTube_Video" + ext;
        }

        return name;
    }
}
