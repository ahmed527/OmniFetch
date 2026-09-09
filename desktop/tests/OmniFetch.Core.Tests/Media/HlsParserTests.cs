using System;
using System.Linq;
using OmniFetch.Core.Media;
using Xunit;

namespace OmniFetch.Core.Tests.Media;

public class HlsParserTests
{
    private readonly HlsParser _parser = new();

    [Fact]
    public void IsMasterPlaylist_DetectsStreamInfTag()
    {
        string master = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-STREAM-INF:BANDWIDTH=1280000,RESOLUTION=1280x720
            low.m3u8
            """;

        string media = """
            #EXTM3U
            #EXT-X-TARGETDURATION:10
            #EXTINF:9.009,
            chunk_0.ts
            #EXT-X-ENDLIST
            """;

        Assert.True(_parser.IsMasterPlaylist(master));
        Assert.False(_parser.IsMasterPlaylist(media));
    }

    [Fact]
    public void ParseMasterPlaylist_ExtractsVariantsAndResolvesBestVariant()
    {
        string manifest = """
            #EXTM3U
            #EXT-X-VERSION:4
            #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio-aac",NAME="English",DEFAULT=YES,URI="audio/en.m3u8"
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360,CODECS="avc1.4d401e,mp4a.40.2"
            360p.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS="avc1.64002a,mp4a.40.2"
            1080p.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=2500000,RESOLUTION=1280x720,CODECS="avc1.4d401f,mp4a.40.2"
            720p.m3u8
            """;

        var master = _parser.ParseMasterPlaylist(manifest, "https://stream.example.com/hls/master.m3u8");

        Assert.Equal(3, master.Variants.Count);
        Assert.Single(master.AudioRenditions);

        var best = master.GetBestVariant();
        Assert.NotNull(best);
        Assert.Equal(1920, best.Width);
        Assert.Equal(1080, best.Height);
        Assert.Equal(5000000, best.Bandwidth);
        Assert.Equal("https://stream.example.com/hls/1080p.m3u8", best.Uri);

        var audio = master.AudioRenditions.First();
        Assert.Equal("English", audio.Name);
        Assert.True(audio.IsDefault);
        Assert.Equal("https://stream.example.com/hls/audio/en.m3u8", audio.Uri);
    }

    [Fact]
    public void ParseMediaPlaylist_ExtractsSegmentsAndCalculatesDuration()
    {
        string manifest = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:10
            #EXT-X-MEDIA-SEQUENCE:100
            #EXTINF:9.009,
            seg_100.ts
            #EXTINF:8.500,
            seg_101.ts
            #EXTINF:9.200,
            https://cdn.other.com/seg_102.ts
            #EXT-X-ENDLIST
            """;

        var media = _parser.ParseMediaPlaylist(manifest, "https://stream.example.com/video/media.m3u8");

        Assert.Equal(10.0, media.TargetDuration);
        Assert.Equal(100, media.MediaSequence);
        Assert.True(media.IsEndList);
        Assert.Equal(3, media.Segments.Count);

        Assert.Equal(0, media.Segments[0].Index);
        Assert.Equal(9.009, media.Segments[0].Duration);
        Assert.Equal("https://stream.example.com/video/seg_100.ts", media.Segments[0].Uri);

        Assert.Equal(1, media.Segments[1].Index);
        Assert.Equal(8.5, media.Segments[1].Duration);
        Assert.Equal("https://stream.example.com/video/seg_101.ts", media.Segments[1].Uri);

        Assert.Equal(2, media.Segments[2].Index);
        Assert.Equal(9.2, media.Segments[2].Duration);
        Assert.Equal("https://cdn.other.com/seg_102.ts", media.Segments[2].Uri);

        Assert.Equal(26.709, Math.Round(media.TotalDuration, 3));
    }

    [Fact]
    public void ParseMediaPlaylist_ParsesAes128EncryptionTags()
    {
        string manifest = """
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:6
            #EXT-X-KEY:METHOD=AES-128,URI="enc.key",IV=0x0123456789ABCDEF0123456789ABCDEF
            #EXTINF:6.0,
            enc_seg_0.ts
            #EXT-X-KEY:METHOD=NONE
            #EXTINF:6.0,
            plain_seg_1.ts
            #EXT-X-ENDLIST
            """;

        var media = _parser.ParseMediaPlaylist(manifest, "https://secure.example.com/hls/list.m3u8");

        Assert.Equal(2, media.Segments.Count);

        var seg0 = media.Segments[0];
        Assert.NotNull(seg0.Encryption);
        Assert.Equal(HlsEncryptionMethod.Aes128, seg0.Encryption.Method);
        Assert.Equal("https://secure.example.com/hls/enc.key", seg0.Encryption.KeyUri);
        Assert.NotNull(seg0.Encryption.Iv);
        Assert.Equal(16, seg0.Encryption.Iv.Length);
        Assert.Equal(0x01, seg0.Encryption.Iv[0]);
        Assert.Equal(0xEF, seg0.Encryption.Iv[15]);

        var seg1 = media.Segments[1];
        Assert.Null(seg1.Encryption);
    }

    [Fact]
    public void ParseMediaPlaylist_ParsesByteRanges()
    {
        string manifest = """
            #EXTM3U
            #EXT-X-VERSION:4
            #EXT-X-TARGETDURATION:5
            #EXT-X-BYTERANGE:1024@0
            #EXTINF:5.0,
            source.mp4
            #EXT-X-BYTERANGE:2048@1024
            #EXTINF:5.0,
            source.mp4
            #EXT-X-ENDLIST
            """;

        var media = _parser.ParseMediaPlaylist(manifest, "https://cdn.example.com/video.m3u8");

        Assert.Equal(2, media.Segments.Count);
        Assert.Equal(1024, media.Segments[0].ByteRangeLength);
        Assert.Equal(0, media.Segments[0].ByteRangeOffset);

        Assert.Equal(2048, media.Segments[1].ByteRangeLength);
        Assert.Equal(1024, media.Segments[1].ByteRangeOffset);
    }
}
