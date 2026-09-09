using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OmniFetch.Core.Ipc;

/// <summary>
/// Payload sent by Chrome Extension when intercepting a file download.
/// </summary>
public sealed class NativeDownloadRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "download";

    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("referrer")]
    public string? Referrer { get; set; }

    [JsonPropertyName("cookies")]
    public string? Cookies { get; set; }

    [JsonPropertyName("userAgent")]
    public string? UserAgent { get; set; }

    [JsonPropertyName("mimeType")]
    public string? MimeType { get; set; }

    [JsonPropertyName("fileSize")]
    public long? FileSize { get; set; }

    [JsonPropertyName("suggestedFileName")]
    public string? SuggestedFileName { get; set; }

    [JsonPropertyName("customHeaders")]
    public Dictionary<string, string>? CustomHeaders { get; set; }
}

/// <summary>
/// Payload sent by Chrome Extension to hot-swap an expired URL.
/// </summary>
public sealed class NativeRefreshUrlRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "refresh_url";

    [JsonPropertyName("jobId")]
    public Guid JobId { get; set; }

    [JsonPropertyName("newUrl")]
    public string NewUrl { get; set; } = string.Empty;

    [JsonPropertyName("cookies")]
    public string? Cookies { get; set; }

    [JsonPropertyName("referrer")]
    public string? Referrer { get; set; }
}

/// <summary>
/// Payload sent to check daemon liveness and health.
/// </summary>
public sealed class NativePingRequest
{
    [JsonPropertyName("action")]
    public string Action { get; set; } = "ping";

    [JsonPropertyName("client")]
    public string? Client { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

/// <summary>
/// Generic or typed response returned from OmniFetch daemon to the bridge and Chrome.
/// </summary>
public sealed class IpcResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("jobId")]
    public Guid? JobId { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public static IpcResponse Ok(string message = "OK") => new()
    {
        Status = "ok",
        Message = message
    };

    public static IpcResponse Accepted(Guid jobId, string? fileName = null, string message = "Download accepted") => new()
    {
        Status = "accepted",
        JobId = jobId,
        FileName = fileName,
        Message = message
    };

    public static IpcResponse Forwarded(string message = "Forwarded to OmniFetch daemon") => new()
    {
        Status = "forwarded",
        Message = message
    };

    public static IpcResponse Error(string errorCode, string message) => new()
    {
        Status = "error",
        ErrorCode = errorCode,
        Message = message
    };
}
