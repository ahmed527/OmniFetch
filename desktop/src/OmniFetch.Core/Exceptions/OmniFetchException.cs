namespace OmniFetch.Core.Exceptions;

/// <summary>
/// Base exception for all OmniFetch domain exceptions.
/// </summary>
public class OmniFetchException : Exception
{
    public OmniFetchException(string message) : base(message) { }
    public OmniFetchException(string message, Exception innerException) : base(message, innerException) { }
}
