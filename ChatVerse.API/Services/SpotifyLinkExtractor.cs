using System.Text.RegularExpressions;
using ChatVerse.Domain.Entities;

namespace ChatVerse.API.Services;

/// <summary>
/// Detects Spotify share / web links inside chat messages and turns them
/// into a renderable <see cref="SpotifyEmbed"/>.
///
/// Why server-side: keeping URL parsing here means the frontend doesn't
/// need to ship a regex bundle or worry about Spotify's many subdomain
/// formats — it just renders <c>message.spotify.embedUrl</c> in an
/// iframe when the field is present. It also means the embed URL is
/// available identically over REST history endpoints, the SignalR
/// broadcast, and the DM path with no duplication.
///
/// Supported URL shapes (all resolve to a track/album/playlist/etc. id):
///   https://open.spotify.com/track/{id}
///   https://open.spotify.com/album/{id}?si=...
///   https://open.spotify.com/playlist/{id}
///   https://open.spotify.com/episode/{id}
///   https://open.spotify.com/show/{id}
///   https://open.spotify.com/artist/{id}
///   spotify:track:{id}              ← native app share format
///   open.spotify.com/embed/track/{id} ← already-embed URLs are normalised
/// </summary>
public static class SpotifyLinkExtractor
{
    // Spotify IDs are 22-char base62 strings. The kind regex is constrained
    // to the official set so a random word like "playlist" elsewhere in
    // text can't false-positive.
    private static readonly Regex WebUrl = new(
        @"https?://(?:open\.)?spotify\.com/(?:embed/)?(track|album|playlist|episode|show|artist)/([A-Za-z0-9]{22})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UriScheme = new(
        @"spotify:(track|album|playlist|episode|show|artist):([A-Za-z0-9]{22})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Returns the first Spotify link in the text as a renderable embed,
    /// or null if no recognisable Spotify URL is present.
    /// We only attach one embed per message (the first match) so a wall
    /// of links doesn't blow up the message DTO size.
    /// </summary>
    public static SpotifyEmbed? Extract(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var m = WebUrl.Match(text);
        if (m.Success)
        {
            var kind = m.Groups[1].Value.ToLowerInvariant();
            var id = m.Groups[2].Value;
            return new SpotifyEmbed
            {
                Kind = kind,
                SpotifyId = id,
                EmbedUrl = $"https://open.spotify.com/embed/{kind}/{id}",
                WebUrl = $"https://open.spotify.com/{kind}/{id}",
            };
        }

        var u = UriScheme.Match(text);
        if (u.Success)
        {
            var kind = u.Groups[1].Value.ToLowerInvariant();
            var id = u.Groups[2].Value;
            return new SpotifyEmbed
            {
                Kind = kind,
                SpotifyId = id,
                EmbedUrl = $"https://open.spotify.com/embed/{kind}/{id}",
                WebUrl = $"https://open.spotify.com/{kind}/{id}",
            };
        }

        return null;
    }
}
