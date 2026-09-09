using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OmniFetch.Core.Logging;

/// <summary>
/// Extension methods to register OmniFetch file logging with ILoggingBuilder.
/// </summary>
public static class OmniFetchLoggingExtensions
{
    public static ILoggingBuilder AddOmniFetchFileLogging(
        this ILoggingBuilder builder,
        Action<OmniFetchLogConfig>? configure = null)
    {
        var config = new OmniFetchLogConfig();
        configure?.Invoke(config);

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton<ILoggerProvider, OmniFetchLoggerProvider>(sp =>
        {
            var cfg = sp.GetService<OmniFetchLogConfig>() ?? config;
            return new OmniFetchLoggerProvider(cfg);
        });

        return builder;
    }
}
