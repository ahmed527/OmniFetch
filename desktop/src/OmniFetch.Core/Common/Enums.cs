namespace OmniFetch.Core.Common;

/// <summary>
/// Status states of a download job matching the OmniFetch lifecycle specification.
/// </summary>
public enum DownloadStatus
{
    Queued = 0,
    Downloading = 1,
    Paused = 2,
    Completed = 3,
    Failed = 4,
    /// <summary>
    /// Signifies that an expiring cloud link (S3, CloudFront, Rapidgator) returned 401/403/410.
    /// Triggers the "Refresh Download Address" workflow.
    /// </summary>
    Expired = 5
}

/// <summary>
/// Indicates whether the remote server supports HTTP byte-range slicing.
/// </summary>
public enum RangeSupportMode
{
    Unknown = 0,
    Supported = 1,
    NotSupported = 2
}
