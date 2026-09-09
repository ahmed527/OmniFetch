using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using OmniFetch.Core.Models;
using OmniFetch.Core.Network;

namespace OmniFetch.Core.Media;

/// <summary>
/// High-throughput parallel segment downloader for HLS video and audio streams.
/// Performs in-flight AES-128 cryptographic decryption and produces sequential transport chunks.
/// </summary>
public class HlsSegmentDownloader : IHlsSegmentDownloader
{
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, byte[]> _keyCache = new();

    public HlsSegmentDownloader(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? SocketsHttpHandlerFactory.CreateClient();
    }

    public async Task<string[]> DownloadSegmentsAsync(
        HlsMediaPlaylist playlist,
        string stagingDirectory,
        DownloadOptions? options = null,
        IProgress<HlsDownloadProgress>? progress = null,
        int maxConcurrency = 8,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);

        options ??= new DownloadOptions();
        int total = playlist.Segments.Count;
        if (total == 0) return [];

        var segmentPaths = new string[total];
        int completedCount = 0;
        long totalDownloadedBytes = 0;

        var stopwatch = Stopwatch.StartNew();
        long lastSpeedBytes = 0;
        long lastSpeedTimeMs = 0;
        double smoothedSpeed = 0;

        for (int i = 0; i < total; i++)
        {
            var segment = playlist.Segments[i];
            segmentPaths[i] = Path.Combine(stagingDirectory, $"segment_{segment.Index:D6}.ts");
        }

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = maxConcurrency,
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(playlist.Segments, parallelOptions, async (segment, token) =>
        {
            string targetPath = segmentPaths[segment.Index];

            // Resume support: if chunk is already downloaded and decrypted, skip
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length > 0)
            {
                Interlocked.Increment(ref completedCount);
                Interlocked.Add(ref totalDownloadedBytes, new FileInfo(targetPath).Length);
                return;
            }

            byte[] rawBytes = await DownloadChunkBytesAsync(segment, options, token).ConfigureAwait(false);

            byte[] finalBytes = rawBytes;
            if (segment.Encryption != null && segment.Encryption.Method == HlsEncryptionMethod.Aes128)
            {
                long seqNum = playlist.MediaSequence + segment.Index;
                finalBytes = await DecryptChunkAsync(rawBytes, segment.Encryption, seqNum, options, token).ConfigureAwait(false);
            }

            await File.WriteAllBytesAsync(targetPath, finalBytes, token).ConfigureAwait(false);

            int done = Interlocked.Increment(ref completedCount);
            long downloaded = Interlocked.Add(ref totalDownloadedBytes, finalBytes.Length);

            // Speed calculation
            long nowMs = stopwatch.ElapsedMilliseconds;
            long elapsedSinceLast = nowMs - Volatile.Read(ref lastSpeedTimeMs);
            if (elapsedSinceLast >= 250) // 4 Hz
            {
                long bytesSinceLast = downloaded - Volatile.Read(ref lastSpeedBytes);
                double instSpeed = bytesSinceLast / (elapsedSinceLast / 1000.0);
                smoothedSpeed = smoothedSpeed <= 0 ? instSpeed : (0.25 * instSpeed + 0.75 * smoothedSpeed);
                Volatile.Write(ref lastSpeedBytes, downloaded);
                Volatile.Write(ref lastSpeedTimeMs, nowMs);

                TimeSpan? eta = null;
                if (smoothedSpeed > 0 && done > 0)
                {
                    double avgSegmentBytes = (double)downloaded / done;
                    long remainingEstBytes = (long)((total - done) * avgSegmentBytes);
                    eta = TimeSpan.FromSeconds(remainingEstBytes / smoothedSpeed);
                }

                progress?.Report(new HlsDownloadProgress
                {
                    CompletedSegments = done,
                    TotalSegments = total,
                    DownloadedBytes = downloaded,
                    SpeedBytesPerSecond = smoothedSpeed,
                    EstimatedTimeRemaining = eta
                });
            }
        }).ConfigureAwait(false);

        // Final 100% progress report
        progress?.Report(new HlsDownloadProgress
        {
            CompletedSegments = total,
            TotalSegments = total,
            DownloadedBytes = totalDownloadedBytes,
            SpeedBytesPerSecond = 0,
            EstimatedTimeRemaining = TimeSpan.Zero
        });

        return segmentPaths;
    }

    private async Task<byte[]> DownloadChunkBytesAsync(HlsSegment segment, DownloadOptions options, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, segment.Uri);

        if (!string.IsNullOrWhiteSpace(options.UserAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);

        if (!string.IsNullOrWhiteSpace(options.Referrer))
            request.Headers.TryAddWithoutValidation("Referer", options.Referrer);

        if (!string.IsNullOrWhiteSpace(options.Cookies))
            request.Headers.TryAddWithoutValidation("Cookie", options.Cookies);

        if (segment.ByteRangeOffset.HasValue && segment.ByteRangeLength.HasValue)
        {
            long from = segment.ByteRangeOffset.Value;
            long to = from + segment.ByteRangeLength.Value - 1;
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(from, to);
        }

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
    }

    private async Task<byte[]> DecryptChunkAsync(
        byte[] encryptedBytes, 
        HlsEncryptionInfo encryption, 
        long sequenceNumber,
        DownloadOptions options, 
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(encryption.KeyUri))
        {
            throw new InvalidOperationException("HLS segment requires AES-128 encryption but KeyUri is missing.");
        }

        byte[] key = await GetOrDownloadKeyAsync(encryption.KeyUri, options, ct).ConfigureAwait(false);
        byte[] iv = encryption.Iv ?? DeriveIvFromSequence(sequenceNumber);

        using var aes = Aes.Create();
        aes.Key = key;
        aes.IV = iv;
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;

        using var decryptor = aes.CreateDecryptor();
        return decryptor.TransformFinalBlock(encryptedBytes, 0, encryptedBytes.Length);
    }

    private static byte[] DeriveIvFromSequence(long sequenceNumber)
    {
        byte[] iv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(iv.AsSpan(8, 8), sequenceNumber);
        return iv;
    }

    private async Task<byte[]> GetOrDownloadKeyAsync(string keyUri, DownloadOptions options, CancellationToken ct)
    {
        if (_keyCache.TryGetValue(keyUri, out var cachedKey))
        {
            return cachedKey;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, keyUri);

        if (!string.IsNullOrWhiteSpace(options.UserAgent))
            request.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);

        if (!string.IsNullOrWhiteSpace(options.Referrer))
            request.Headers.TryAddWithoutValidation("Referer", options.Referrer);

        if (!string.IsNullOrWhiteSpace(options.Cookies))
            request.Headers.TryAddWithoutValidation("Cookie", options.Cookies);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        byte[] keyBytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        if (keyBytes.Length != 16)
        {
            throw new InvalidOperationException($"Invalid HLS AES-128 key length: expected 16 bytes, got {keyBytes.Length} bytes from {keyUri}");
        }

        _keyCache[keyUri] = keyBytes;
        return keyBytes;
    }
}
