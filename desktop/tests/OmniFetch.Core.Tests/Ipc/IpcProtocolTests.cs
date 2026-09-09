using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using OmniFetch.Core.Ipc;
using Xunit;

namespace OmniFetch.Core.Tests.Ipc;

public class IpcProtocolTests
{
    [Fact]
    public void NativeDownloadRequest_Serialization_RoundtripsAccurately()
    {
        var request = new NativeDownloadRequest
        {
            Action = "download",
            Url = "https://speed.hetzner.de/100MB.bin",
            Referrer = "https://hetzner.com/downloads",
            Cookies = "session_id=xyz789; token=abc",
            UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7)",
            MimeType = "application/octet-stream",
            FileSize = 104857600,
            SuggestedFileName = "100MB.bin",
            CustomHeaders = new Dictionary<string, string>
            {
                { "Authorization", "Bearer my_secret_token" }
            }
        };

        string json = JsonSerializer.Serialize(request, IpcJsonSerializerContext.Default.NativeDownloadRequest);
        Assert.Contains("\"url\":\"https://speed.hetzner.de/100MB.bin\"", json);
        Assert.Contains("\"suggestedFileName\":\"100MB.bin\"", json);

        var deserialized = JsonSerializer.Deserialize(json, IpcJsonSerializerContext.Default.NativeDownloadRequest);
        Assert.NotNull(deserialized);
        Assert.Equal(request.Url, deserialized.Url);
        Assert.Equal(request.Referrer, deserialized.Referrer);
        Assert.Equal(request.Cookies, deserialized.Cookies);
        Assert.Equal(request.UserAgent, deserialized.UserAgent);
        Assert.Equal(request.MimeType, deserialized.MimeType);
        Assert.Equal(request.FileSize, deserialized.FileSize);
        Assert.Equal(request.SuggestedFileName, deserialized.SuggestedFileName);
        Assert.NotNull(deserialized.CustomHeaders);
        Assert.Equal("Bearer my_secret_token", deserialized.CustomHeaders["Authorization"]);
    }

    [Fact]
    public void NativeRefreshUrlRequest_Serialization_RoundtripsAccurately()
    {
        var jobId = Guid.NewGuid();
        var request = new NativeRefreshUrlRequest
        {
            Action = "refresh_url",
            JobId = jobId,
            NewUrl = "https://s3.amazonaws.com/bucket/file.zip?new-signature=123",
            Cookies = "s3_cookie=valid",
            Referrer = "https://s3.amazonaws.com"
        };

        string json = JsonSerializer.Serialize(request, IpcJsonSerializerContext.Default.NativeRefreshUrlRequest);
        Assert.Contains(jobId.ToString(), json);

        var deserialized = JsonSerializer.Deserialize(json, IpcJsonSerializerContext.Default.NativeRefreshUrlRequest);
        Assert.NotNull(deserialized);
        Assert.Equal(jobId, deserialized.JobId);
        Assert.Equal(request.NewUrl, deserialized.NewUrl);
        Assert.Equal(request.Cookies, deserialized.Cookies);
    }

    [Fact]
    public void NativePingRequest_Serialization_RoundtripsAccurately()
    {
        var request = new NativePingRequest
        {
            Action = "ping",
            Client = "OmniFetch.Bridge"
        };

        string json = JsonSerializer.Serialize(request, IpcJsonSerializerContext.Default.NativePingRequest);
        var deserialized = JsonSerializer.Deserialize(json, IpcJsonSerializerContext.Default.NativePingRequest);

        Assert.NotNull(deserialized);
        Assert.Equal("ping", deserialized.Action);
        Assert.Equal("OmniFetch.Bridge", deserialized.Client);
        Assert.True(deserialized.Timestamp > 0);
    }

    [Fact]
    public void IpcResponse_FactoryMethods_ProduceExpectedPayloads()
    {
        var ok = IpcResponse.Ok("All systems go");
        Assert.Equal("ok", ok.Status);
        Assert.Equal("All systems go", ok.Message);

        var jobId = Guid.NewGuid();
        var accepted = IpcResponse.Accepted(jobId, "Archive.zip");
        Assert.Equal("accepted", accepted.Status);
        Assert.Equal(jobId, accepted.JobId);
        Assert.Equal("Archive.zip", accepted.FileName);

        var err = IpcResponse.Error("DOWNLOAD_FAILED", "Timeout connecting to host");
        Assert.Equal("error", err.Status);
        Assert.Equal("DOWNLOAD_FAILED", err.ErrorCode);
        Assert.Equal("Timeout connecting to host", err.Message);
    }

    [Fact]
    public void LittleEndianFraming_EncodesAndDecodesCorrectLength()
    {
        string payload = "{\"status\":\"ok\"}";
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

        byte[] frame = new byte[4 + payloadBytes.Length];
        BinaryPrimitives.WriteInt32LittleEndian(frame.AsSpan(0, 4), payloadBytes.Length);
        payloadBytes.CopyTo(frame.AsSpan(4));

        int decodedLength = BinaryPrimitives.ReadInt32LittleEndian(frame.AsSpan(0, 4));
        Assert.Equal(payloadBytes.Length, decodedLength);

        string decodedPayload = Encoding.UTF8.GetString(frame.AsSpan(4));
        Assert.Equal(payload, decodedPayload);
    }
}
