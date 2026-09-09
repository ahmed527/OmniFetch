using System;
using System.Collections.Generic;
using System.Linq;

namespace OmniFetch.Core.Media;

/// <summary>
/// Supported encryption algorithms in HLS (RFC 8216).
/// </summary>
public enum HlsEncryptionMethod
{
    None,
    Aes128,
    SampleAes
}

/// <summary>
/// Encryption metadata extracted from #EXT-X-KEY tags.
/// </summary>
public record HlsEncryptionInfo
{
    public HlsEncryptionMethod Method { get; init; } = HlsEncryptionMethod.None;
    public string? KeyUri { get; init; }
    public byte[]? Iv { get; init; }
}

/// <summary>
/// Represents an individual video/audio chunk in an HLS media playlist (#EXTINF).
/// </summary>
public class HlsSegment
{
    public int Index { get; init; }
    public double Duration { get; init; }
    public string Uri { get; init; } = string.Empty;
    public long? ByteRangeOffset { get; init; }
    public long? ByteRangeLength { get; init; }
    public HlsEncryptionInfo? Encryption { get; init; }
}

/// <summary>
/// Represents a quality variant in an HLS master playlist (#EXT-X-STREAM-INF).
/// </summary>
public class HlsStreamVariant
{
    public int Bandwidth { get; init; }
    public int? Width { get; init; }
    public int? Height { get; init; }
    public string? Codecs { get; init; }
    public string Uri { get; init; } = string.Empty;

    public string Resolution => (Width.HasValue && Height.HasValue) ? $"{Width}x{Height}" : "Unknown";
}

/// <summary>
/// Rendition metadata for separate audio or subtitle tracks (#EXT-X-MEDIA).
/// </summary>
public class HlsMediaRendition
{
    public string Type { get; init; } = "AUDIO";
    public string GroupId { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? Language { get; init; }
    public string? Uri { get; init; }
    public bool IsDefault { get; init; }
    public bool IsAutoSelect { get; init; }
}

/// <summary>
/// Master playlist containing multiple quality variants and renditions.
/// </summary>
public class HlsMasterPlaylist
{
    public string BaseUri { get; init; } = string.Empty;
    public List<HlsStreamVariant> Variants { get; init; } = [];
    public List<HlsMediaRendition> Renditions { get; init; } = [];

    public IEnumerable<HlsMediaRendition> AudioRenditions => 
        Renditions.Where(r => string.Equals(r.Type, "AUDIO", StringComparison.OrdinalIgnoreCase));

    public HlsStreamVariant? GetBestVariant()
    {
        return Variants
            .OrderByDescending(v => v.Height ?? 0)
            .ThenByDescending(v => v.Bandwidth)
            .FirstOrDefault();
    }
}

/// <summary>
/// Media playlist containing the sequential list of playable chunks.
/// </summary>
public class HlsMediaPlaylist
{
    public string BaseUri { get; init; } = string.Empty;
    public double TargetDuration { get; init; }
    public long MediaSequence { get; init; }
    public List<HlsSegment> Segments { get; init; } = [];
    public bool IsEndList { get; init; }

    public double TotalDuration => Segments.Sum(s => s.Duration);
}
