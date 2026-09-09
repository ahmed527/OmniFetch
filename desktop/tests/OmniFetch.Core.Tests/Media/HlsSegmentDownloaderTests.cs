using System;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OmniFetch.Core.Media;
using Xunit;

namespace OmniFetch.Core.Tests.Media;

public class HlsSegmentDownloaderTests : IDisposable
{
    private readonly string _tempDirectory;

    public HlsSegmentDownloaderTests()
    {
        _tempDirectory = Path.Combine(Path.GetTempPath(), "OmniFetch_HlsDownloaderTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDirectory);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDirectory))
            {
                Directory.Delete(_tempDirectory, recursive: true);
            }
        }
        catch { }
    }

    [Fact]
    public async Task DownloadSegmentsAsync_PlainSegments_DownloadsAllSuccessfully()
    {
        var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            string url = request.RequestUri!.ToString();
            byte[] content = Encoding.UTF8.GetBytes($"CONTENT_FOR_{url}");
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            return resp;
        });

        using var client = new HttpClient(handler);
        var downloader = new HlsSegmentDownloader(client);

        var playlist = new HlsMediaPlaylist
        {
            BaseUri = "https://cdn.example.com/",
            TargetDuration = 5,
            Segments =
            [
                new HlsSegment { Index = 0, Duration = 5.0, Uri = "https://cdn.example.com/seg0.ts" },
                new HlsSegment { Index = 1, Duration = 5.0, Uri = "https://cdn.example.com/seg1.ts" },
                new HlsSegment { Index = 2, Duration = 5.0, Uri = "https://cdn.example.com/seg2.ts" }
            ]
        };

        var progressReported = 0;
        var progress = new Progress<HlsDownloadProgress>(p =>
        {
            Interlocked.Increment(ref progressReported);
        });

        var paths = await downloader.DownloadSegmentsAsync(
            playlist,
            _tempDirectory,
            progress: progress,
            maxConcurrency: 4);

        Assert.Equal(3, paths.Length);
        for (int i = 0; i < 3; i++)
        {
            Assert.True(File.Exists(paths[i]));
            string downloaded = await File.ReadAllTextAsync(paths[i]);
            Assert.Equal($"CONTENT_FOR_https://cdn.example.com/seg{i}.ts", downloaded);
        }
        Assert.True(progressReported > 0);
    }

    [Fact]
    public async Task DownloadSegmentsAsync_Aes128Encrypted_DecryptsToExactPlaintext()
    {
        // 1. Setup AES-128 key and explicit IV
        byte[] key = RandomNumberGenerator.GetBytes(16);
        byte[] iv = RandomNumberGenerator.GetBytes(16);

        // 2. Create plaintext payload (e.g. 512 bytes with standard MPEG-TS sync pattern 0x47)
        byte[] plaintext = new byte[512];
        for (int i = 0; i < plaintext.Length; i++)
        {
            plaintext[i] = (byte)(i % 188 == 0 ? 0x47 : (i & 0xFF));
        }

        // 3. Encrypt payload with AES-128-CBC and PKCS7 padding
        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = iv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        // 4. Mock HTTP handler serving key and ciphertext
        var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            string url = request.RequestUri!.ToString();
            if (url == "https://secure.example.com/keys/key1.bin")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(key) };
            }
            if (url == "https://secure.example.com/video/seg_enc_0.ts")
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ciphertext) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var downloader = new HlsSegmentDownloader(client);

        var playlist = new HlsMediaPlaylist
        {
            BaseUri = "https://secure.example.com/video/",
            TargetDuration = 6,
            Segments =
            [
                new HlsSegment
                {
                    Index = 0,
                    Duration = 6.0,
                    Uri = "https://secure.example.com/video/seg_enc_0.ts",
                    Encryption = new HlsEncryptionInfo
                    {
                        Method = HlsEncryptionMethod.Aes128,
                        KeyUri = "https://secure.example.com/keys/key1.bin",
                        Iv = iv
                    }
                }
            ]
        };

        var paths = await downloader.DownloadSegmentsAsync(playlist, _tempDirectory);

        Assert.Single(paths);
        Assert.True(File.Exists(paths[0]));

        byte[] decryptedOnDisk = await File.ReadAllBytesAsync(paths[0]);
        Assert.Equal(plaintext.Length, decryptedOnDisk.Length);
        Assert.Equal(plaintext, decryptedOnDisk);
    }

    [Fact]
    public async Task DownloadSegmentsAsync_Aes128ImplicitIv_DerivesIvFromSequenceNumber()
    {
        // RFC 8216 Section 5.2: If the EXT-X-KEY tag has the KEYFORMAT "identity" and the IV attribute is not present,
        // the sequence number of the media segment MUST be used as the IV attribute value, formatted as a 16-octet big-endian integer.
        long sequenceNumber = 42;
        byte[] key = RandomNumberGenerator.GetBytes(16);
        byte[] expectedIv = new byte[16];
        BinaryPrimitives.WriteInt64BigEndian(expectedIv.AsSpan(8, 8), sequenceNumber);

        byte[] plaintext = Encoding.UTF8.GetBytes("RFC-8216-IMPLICIT-IV-VERIFICATION-PAYLOAD-1234567890");

        byte[] ciphertext;
        using (var aes = Aes.Create())
        {
            aes.Key = key;
            aes.IV = expectedIv;
            aes.Mode = CipherMode.CBC;
            aes.Padding = PaddingMode.PKCS7;
            using var encryptor = aes.CreateEncryptor();
            ciphertext = encryptor.TransformFinalBlock(plaintext, 0, plaintext.Length);
        }

        var handler = new MockHttpMessageHandler(async (request, ct) =>
        {
            string url = request.RequestUri!.ToString();
            if (url.EndsWith("implicit.key"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(key) };
            }
            if (url.EndsWith("seg_implicit_42.ts"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(ciphertext) };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });

        using var client = new HttpClient(handler);
        var downloader = new HlsSegmentDownloader(client);

        var playlist = new HlsMediaPlaylist
        {
            BaseUri = "https://hls.example.com/",
            MediaSequence = sequenceNumber,
            TargetDuration = 6,
            Segments =
            [
                new HlsSegment
                {
                    Index = 0,
                    Duration = 6.0,
                    Uri = "https://hls.example.com/seg_implicit_42.ts",
                    Encryption = new HlsEncryptionInfo
                    {
                        Method = HlsEncryptionMethod.Aes128,
                        KeyUri = "https://hls.example.com/implicit.key",
                        Iv = null // Implicit IV: MUST derive from MediaSequence + Index (42 + 0 = 42)
                    }
                }
            ]
        };

        var paths = await downloader.DownloadSegmentsAsync(playlist, _tempDirectory);

        Assert.Single(paths);
        byte[] decryptedOnDisk = await File.ReadAllBytesAsync(paths[0]);
        Assert.Equal(plaintext, decryptedOnDisk);
    }

    private class MockHttpMessageHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handlerFunc) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return handlerFunc(request, cancellationToken);
        }
    }
}
