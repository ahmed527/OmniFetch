using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace OmniFetch.Bridge;

public sealed class BridgeResponse
{
    [JsonPropertyName("status")]
    public string Status { get; set; } = string.Empty;

    [JsonPropertyName("errorCode")]
    public string? ErrorCode { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("timestamp")]
    public long Timestamp { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
}

public sealed class NativeHostManifest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "com.omnifetch.bridge";

    [JsonPropertyName("description")]
    public string Description { get; set; } = "OmniFetch Native Messaging IPC Bridge";

    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; set; } = "stdio";

    [JsonPropertyName("allowed_origins")]
    public List<string> AllowedOrigins { get; set; } = new();
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(BridgeResponse))]
[JsonSerializable(typeof(NativeHostManifest))]
public partial class BridgeJsonContext : JsonSerializerContext
{
}
