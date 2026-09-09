using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace OmniFetch.Core.Logging;

/// <summary>
/// Provider for OmniFetch file loggers.
/// </summary>
public sealed class OmniFetchLoggerProvider : ILoggerProvider
{
    private readonly OmniFetchLogConfig _config;
    private readonly ConcurrentDictionary<string, OmniFetchFileLogger> _loggers = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public OmniFetchLoggerProvider(OmniFetchLogConfig? config = null)
    {
        _config = config ?? new OmniFetchLogConfig();
    }

    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _loggers.GetOrAdd(categoryName, name => new OmniFetchFileLogger(name, _config));
    }

    public void Dispose()
    {
        _disposed = true;
        _loggers.Clear();
    }
}
