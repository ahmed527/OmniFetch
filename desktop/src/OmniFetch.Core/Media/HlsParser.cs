using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OmniFetch.Core.Media;

/// <summary>
/// Conforming RFC 8216 parser for HLS Master Playlists and Media Playlists.
/// Handles relative URI resolution, stream quality variants, byte-ranges, and AES-128 keys.
/// </summary>
public partial class HlsParser : IHlsParser
{
    private static readonly Regex StreamInfRegex = new(@"#EXT-X-STREAM-INF:(.+)", RegexOptions.Compiled);
    private static readonly Regex BandwidthRegex = new(@"BANDWIDTH=(\d+)", RegexOptions.Compiled);
    private static readonly Regex ResolutionRegex = new(@"RESOLUTION=(\d+)x(\d+)", RegexOptions.Compiled);
    private static readonly Regex CodecsRegex = new(@"CODECS=""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex MediaTypeRegex = new(@"TYPE=([A-Za-z0-9-]+)", RegexOptions.Compiled);
    private static readonly Regex MediaGroupIdRegex = new(@"GROUP-ID=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex MediaNameRegex = new(@"NAME=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex MediaDefaultRegex = new(@"DEFAULT=(YES|NO)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MediaAutoSelectRegex = new(@"AUTOSELECT=(YES|NO)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex MediaLanguageRegex = new(@"LANGUAGE=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex MediaUriRegex = new(@"URI=""([^""]+)""", RegexOptions.Compiled);

    private static readonly Regex TargetDurationRegex = new(@"#EXT-X-TARGETDURATION:([0-9.]+)", RegexOptions.Compiled);
    private static readonly Regex MediaSequenceRegex = new(@"#EXT-X-MEDIA-SEQUENCE:(\d+)", RegexOptions.Compiled);
    private static readonly Regex ExtInfRegex = new(@"#EXTINF:([0-9.]+)", RegexOptions.Compiled);
    private static readonly Regex KeyRegex = new(@"#EXT-X-KEY:(.+)", RegexOptions.Compiled);
    private static readonly Regex KeyMethodRegex = new(@"METHOD=([A-Za-z0-9-]+)", RegexOptions.Compiled);
    private static readonly Regex KeyUriRegex = new(@"URI=""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex KeyIvRegex = new(@"IV=0[xX]([0-9a-fA-F]+)", RegexOptions.Compiled);
    private static readonly Regex ByteRangeRegex = new(@"#EXT-X-BYTERANGE:(\d+)(?:@(\d+))?", RegexOptions.Compiled);

    public bool IsMasterPlaylist(string playlistContent)
    {
        return playlistContent.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);
    }

    public HlsMasterPlaylist ParseMasterPlaylist(string content, string baseUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var variants = new List<HlsStreamVariant>();
        var renditions = new List<HlsMediaRendition>();

        using var reader = new StringReader(content);
        string? line;
        HlsStreamVariant? pendingVariant = null;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.OrdinalIgnoreCase))
            {
                string type = "AUDIO";
                var typeMatch = MediaTypeRegex.Match(line);
                if (typeMatch.Success) type = typeMatch.Groups[1].Value.ToUpperInvariant();

                string groupId = string.Empty;
                var groupMatch = MediaGroupIdRegex.Match(line);
                if (groupMatch.Success) groupId = groupMatch.Groups[1].Value;

                string name = string.Empty;
                var nameMatch = MediaNameRegex.Match(line);
                if (nameMatch.Success) name = nameMatch.Groups[1].Value;

                string? language = null;
                var langMatch = MediaLanguageRegex.Match(line);
                if (langMatch.Success) language = langMatch.Groups[1].Value;

                string? uri = null;
                var uriMatch = MediaUriRegex.Match(line);
                if (uriMatch.Success) uri = ResolveUri(baseUri, uriMatch.Groups[1].Value);

                bool isDefault = false;
                var defMatch = MediaDefaultRegex.Match(line);
                if (defMatch.Success) isDefault = string.Equals(defMatch.Groups[1].Value, "YES", StringComparison.OrdinalIgnoreCase);

                bool isAutoSelect = false;
                var autoMatch = MediaAutoSelectRegex.Match(line);
                if (autoMatch.Success) isAutoSelect = string.Equals(autoMatch.Groups[1].Value, "YES", StringComparison.OrdinalIgnoreCase);

                renditions.Add(new HlsMediaRendition
                {
                    Type = type,
                    GroupId = groupId,
                    Name = name,
                    Language = language,
                    Uri = uri,
                    IsDefault = isDefault,
                    IsAutoSelect = isAutoSelect
                });
            }
            else if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
            {
                int bandwidth = 0;
                int? width = null;
                int? height = null;
                string? codecs = null;

                var bwMatch = BandwidthRegex.Match(line);
                if (bwMatch.Success && int.TryParse(bwMatch.Groups[1].Value, out int bw))
                {
                    bandwidth = bw;
                }

                var resMatch = ResolutionRegex.Match(line);
                if (resMatch.Success)
                {
                    if (int.TryParse(resMatch.Groups[1].Value, out int w)) width = w;
                    if (int.TryParse(resMatch.Groups[2].Value, out int h)) height = h;
                }

                var codecsMatch = CodecsRegex.Match(line);
                if (codecsMatch.Success)
                {
                    codecs = codecsMatch.Groups[1].Value;
                }

                pendingVariant = new HlsStreamVariant
                {
                    Bandwidth = bandwidth,
                    Width = width,
                    Height = height,
                    Codecs = codecs
                };
            }
            else if (!line.StartsWith('#') && pendingVariant != null)
            {
                string resolvedUri = ResolveUri(baseUri, line);
                variants.Add(new HlsStreamVariant
                {
                    Bandwidth = pendingVariant.Bandwidth,
                    Width = pendingVariant.Width,
                    Height = pendingVariant.Height,
                    Codecs = pendingVariant.Codecs,
                    Uri = resolvedUri
                });
                pendingVariant = null;
            }
        }

        return new HlsMasterPlaylist
        {
            BaseUri = baseUri,
            Variants = variants,
            Renditions = renditions
        };
    }

    public HlsMediaPlaylist ParseMediaPlaylist(string content, string baseUri)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);
        var segments = new List<HlsSegment>();
        double targetDuration = 0;
        long mediaSequence = 0;
        bool isEndList = false;

        var tdMatch = TargetDurationRegex.Match(content);
        if (tdMatch.Success && double.TryParse(tdMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double td))
        {
            targetDuration = td;
        }

        var msMatch = MediaSequenceRegex.Match(content);
        if (msMatch.Success && long.TryParse(msMatch.Groups[1].Value, out long ms))
        {
            mediaSequence = ms;
        }

        HlsEncryptionInfo? currentEncryption = null;
        double currentDuration = 0;
        long? currentByteRangeLength = null;
        long? currentByteRangeOffset = null;
        long cumulativeOffset = 0;
        int segmentIndex = 0;

        using var reader = new StringReader(content);
        string? line;

        while ((line = reader.ReadLine()) != null)
        {
            line = line.Trim();
            if (string.IsNullOrEmpty(line)) continue;

            if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                currentEncryption = ParseEncryptionKey(line, baseUri);
            }
            else if (line.StartsWith("#EXT-X-BYTERANGE:", StringComparison.OrdinalIgnoreCase))
            {
                var brMatch = ByteRangeRegex.Match(line);
                if (brMatch.Success)
                {
                    currentByteRangeLength = long.Parse(brMatch.Groups[1].Value);
                    if (brMatch.Groups[2].Success)
                    {
                        currentByteRangeOffset = long.Parse(brMatch.Groups[2].Value);
                        cumulativeOffset = currentByteRangeOffset.Value + currentByteRangeLength.Value;
                    }
                    else
                    {
                        currentByteRangeOffset = cumulativeOffset;
                        cumulativeOffset += currentByteRangeLength.Value;
                    }
                }
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var infMatch = ExtInfRegex.Match(line);
                if (infMatch.Success && double.TryParse(infMatch.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double dur))
                {
                    currentDuration = dur;
                }
            }
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.OrdinalIgnoreCase))
            {
                isEndList = true;
            }
            else if (!line.StartsWith('#'))
            {
                string segmentUri = ResolveUri(baseUri, line);
                HlsEncryptionInfo? segEnc = currentEncryption;

                // RFC 8216: If IV is not specified, it is derived from sequence number as big-endian 16-byte integer
                if (segEnc != null && segEnc.Method == HlsEncryptionMethod.Aes128 && segEnc.Iv == null)
                {
                    long seq = mediaSequence + segmentIndex;
                    byte[] derivedIv = new byte[16];
                    System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(derivedIv.AsSpan(8, 8), seq);
                    segEnc = segEnc with { Iv = derivedIv };
                }

                segments.Add(new HlsSegment
                {
                    Index = segmentIndex++,
                    Duration = currentDuration,
                    Uri = segmentUri,
                    ByteRangeLength = currentByteRangeLength,
                    ByteRangeOffset = currentByteRangeOffset,
                    Encryption = segEnc
                });

                currentByteRangeLength = null;
                currentByteRangeOffset = null;
                currentDuration = 0;
            }
        }

        return new HlsMediaPlaylist
        {
            BaseUri = baseUri,
            TargetDuration = targetDuration,
            MediaSequence = mediaSequence,
            Segments = segments,
            IsEndList = isEndList
        };
    }

    public async Task<HlsMediaPlaylist> ResolveMediaPlaylistAsync(
        string manifestUrl, 
        HttpClient? httpClient = null, 
        CancellationToken ct = default)
    {
        httpClient ??= Network.SocketsHttpHandlerFactory.CreateClient();
        string manifestContent = await httpClient.GetStringAsync(manifestUrl, ct).ConfigureAwait(false);

        if (IsMasterPlaylist(manifestContent))
        {
            var master = ParseMasterPlaylist(manifestContent, manifestUrl);
            var best = master.GetBestVariant() ?? throw new InvalidOperationException("No playable variant streams found in master playlist.");
            string mediaContent = await httpClient.GetStringAsync(best.Uri, ct).ConfigureAwait(false);
            return ParseMediaPlaylist(mediaContent, best.Uri);
        }

        return ParseMediaPlaylist(manifestContent, manifestUrl);
    }

    private static HlsEncryptionInfo? ParseEncryptionKey(string keyTag, string baseUri)
    {
        var methodMatch = KeyMethodRegex.Match(keyTag);
        if (!methodMatch.Success) return null;

        string methodStr = methodMatch.Groups[1].Value.ToUpperInvariant();
        var method = methodStr switch
        {
            "AES-128" => HlsEncryptionMethod.Aes128,
            "SAMPLE-AES" => HlsEncryptionMethod.SampleAes,
            _ => HlsEncryptionMethod.None
        };

        if (method == HlsEncryptionMethod.None) return null;

        string? keyUri = null;
        var uriMatch = KeyUriRegex.Match(keyTag);
        if (uriMatch.Success)
        {
            keyUri = ResolveUri(baseUri, uriMatch.Groups[1].Value);
        }

        byte[]? iv = null;
        var ivMatch = KeyIvRegex.Match(keyTag);
        if (ivMatch.Success)
        {
            string hex = ivMatch.Groups[1].Value;
            if (hex.Length % 2 != 0) hex = "0" + hex;
            iv = Convert.FromHexString(hex);
            if (iv.Length < 16)
            {
                byte[] padded = new byte[16];
                Array.Copy(iv, 0, padded, 16 - iv.Length, iv.Length);
                iv = padded;
            }
        }

        return new HlsEncryptionInfo
        {
            Method = method,
            KeyUri = keyUri,
            Iv = iv
        };
    }

    public static string ResolveUri(string baseUri, string relativeOrAbsoluteUri)
    {
        if (Uri.TryCreate(relativeOrAbsoluteUri, UriKind.Absolute, out var abs))
        {
            return abs.ToString();
        }

        if (Uri.TryCreate(new Uri(baseUri), relativeOrAbsoluteUri, out var resolved))
        {
            return resolved.ToString();
        }

        return relativeOrAbsoluteUri;
    }
}
