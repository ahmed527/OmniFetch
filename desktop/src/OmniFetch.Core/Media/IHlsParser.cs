using System.Threading;
using System.Threading.Tasks;

namespace OmniFetch.Core.Media;

public interface IHlsParser
{
    bool IsMasterPlaylist(string playlistContent);
    HlsMasterPlaylist ParseMasterPlaylist(string content, string baseUri);
    HlsMediaPlaylist ParseMediaPlaylist(string content, string baseUri);
    Task<HlsMediaPlaylist> ResolveMediaPlaylistAsync(string manifestUrl, HttpClient? httpClient = null, CancellationToken ct = default);
}
