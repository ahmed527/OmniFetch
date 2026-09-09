using System.IO;
using OmniFetch.Core.Common;
using Xunit;

namespace OmniFetch.Core.Tests;

public class MimeTypeMapTests
{
    [Theory]
    [InlineData("video/mp4", ".mp4")]
    [InlineData("video/webm", ".webm")]
    [InlineData("video/x-matroska", ".mkv")]
    [InlineData("audio/mp4", ".m4a")]
    [InlineData("audio/mpeg", ".mp3")]
    [InlineData("application/pdf", ".pdf")]
    [InlineData("application/zip", ".zip")]
    [InlineData("video/mp4; codecs=\"avc1.640028\"", ".mp4")]
    public void GetExtension_Returns_Expected_Extension(string mimeType, string expected)
    {
        var ext = MimeTypeMap.GetExtension(mimeType);
        Assert.Equal(expected, ext);
    }

    [Fact]
    public void SniffMimeFromUrl_Extracts_Mime_Param()
    {
        string url = "https://rr1---sn-xxx.googlevideo.com/videoplayback?expire=123&mime=video%2Fmp4&itag=18";
        string? mime = MimeTypeMap.SniffMimeFromUrl(url);
        Assert.Equal("video/mp4", mime);
    }

    [Theory]
    [InlineData("videoplayback.dat", "video/mp4", "https://rr1---sn-xxx.googlevideo.com/videoplayback?mime=video%2Fmp4", "YouTube_Video.mp4")]
    [InlineData("videoplayback", "video/webm", "https://rr1---sn-xxx.googlevideo.com/videoplayback", "YouTube_Video.webm")]
    [InlineData("sample_file.dat", "application/pdf", "https://example.com/file?id=1", "sample_file.pdf")]
    [InlineData("archive.bin", "application/zip", "https://example.com/archive", "archive.zip")]
    [InlineData("Rick_Astley_Never_Gonna_Give_You_Up.dat", "video/mp4", "https://googlevideo.com/videoplayback", "Rick_Astley_Never_Gonna_Give_You_Up.mp4")]
    public void SanitizeAndEnsureExtension_Replaces_Dummy_Extensions(string rawName, string contentType, string url, string expected)
    {
        string sanitized = MimeTypeMap.SanitizeAndEnsureExtension(rawName, contentType, url);
        Assert.Equal(expected, sanitized);
    }

    [Fact]
    public void SanitizeAndEnsureExtension_Preserves_Valid_Extension()
    {
        string sanitized = MimeTypeMap.SanitizeAndEnsureExtension("document.docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", "https://example.com/doc");
        Assert.Equal("document.docx", sanitized);
    }
}
