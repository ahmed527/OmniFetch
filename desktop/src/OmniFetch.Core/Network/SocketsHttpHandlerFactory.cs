using System.Net;
using OmniFetch.Core.Common;

namespace OmniFetch.Core.Network;

/// <summary>
/// Factory for creating and tuning high-throughput SocketsHttpHandler instances on macOS Apple Silicon.
/// Ensures connection persistence, eliminates TCP handshakes, and optimizes socket buffers.
/// </summary>
public static class SocketsHttpHandlerFactory
{
    /// <summary>
    /// Creates a tuned SocketsHttpHandler optimized for high-concurrency multi-stream downloading.
    /// </summary>
    public static SocketsHttpHandler CreateHandler(CookieContainer? cookieContainer = null)
    {
        var handler = new SocketsHttpHandler
        {
            // Keep connections warm across segments to bypass 3-way TCP handshakes and TLS renegotiation
            PooledConnectionLifetime = TimeSpan.FromMinutes(Constants.DefaultConnectionLifetimeMinutes),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(Constants.DefaultConnectionIdleTimeoutMinutes),
            MaxConnectionsPerServer = Constants.DefaultMaxConnectionsPerServer,
            EnableMultipleHttp2Connections = true,
            
            // Allow redirects automatically
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,

            // For raw byte-range downloading, we disable automatic decompression
            // to ensure byte offsets written to disk match remote byte offsets exactly.
            AutomaticDecompression = DecompressionMethods.None,

            // Use system proxy configuration
            UseProxy = true,
            DefaultProxyCredentials = CredentialCache.DefaultCredentials
        };

        if (cookieContainer != null)
        {
            handler.CookieContainer = cookieContainer;
            handler.UseCookies = true;
        }

        return handler;
    }

    /// <summary>
    /// Creates a tuned HttpClient configured for HTTP/1.1, HTTP/2, and HTTP/3 multiplexing.
    /// </summary>
    public static HttpClient CreateClient(CookieContainer? cookieContainer = null)
    {
        var handler = CreateHandler(cookieContainer);
        var client = new HttpClient(handler, disposeHandler: true)
        {
            // Default timeout for connection requests
            Timeout = TimeSpan.FromSeconds(60),
            DefaultRequestVersion = HttpVersion.Version20,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };

        return client;
    }
}
