namespace OmniFetch.Core.Exceptions;

/// <summary>
/// Thrown when dynamic segmentation is requested on a server that strictly rejects range requests.
/// </summary>
public class RangeNotSupportedException : OmniFetchException
{
    public RangeNotSupportedException(string message) : base(message) { }
    public RangeNotSupportedException(string message, Exception innerException) : base(message, innerException) { }
}
