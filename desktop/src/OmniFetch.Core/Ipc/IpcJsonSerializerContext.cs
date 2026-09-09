using System.Text.Json.Serialization;

namespace OmniFetch.Core.Ipc;

/// <summary>
/// Source-generated JsonSerializerContext for zero-reflection, high-speed JSON serialization.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(NativeDownloadRequest))]
[JsonSerializable(typeof(NativeRefreshUrlRequest))]
[JsonSerializable(typeof(NativePingRequest))]
[JsonSerializable(typeof(IpcResponse))]
public partial class IpcJsonSerializerContext : JsonSerializerContext
{
}
