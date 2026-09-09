namespace OmniFetch.Core.Exceptions;

/// <summary>
/// Thrown when file pre-allocation or disk writing fails (e.g. disk full, permission denied).
/// </summary>
public class DiskAllocationException : OmniFetchException
{
    public string FilePath { get; }
    public long RequestedBytes { get; }

    public DiskAllocationException(string filePath, long requestedBytes, string message, Exception? innerException = null)
        : base(message, innerException!)
    {
        FilePath = filePath;
        RequestedBytes = requestedBytes;
    }
}
