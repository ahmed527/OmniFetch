using OmniFetch.Core.Models;

namespace OmniFetch.Core.Exceptions;

/// <summary>
/// Thrown when a download is paused or cancelled via CancellationToken.
/// Contains the DownloadJobInfo with current segment offsets preserved for seamless resumption.
/// </summary>
public class DownloadPausedException : OperationCanceledException
{
    public DownloadJobInfo JobInfo { get; }

    public DownloadPausedException(DownloadJobInfo jobInfo, CancellationToken cancellationToken)
        : base("Download was paused or cancelled by user request.", cancellationToken)
    {
        JobInfo = jobInfo;
    }
}
