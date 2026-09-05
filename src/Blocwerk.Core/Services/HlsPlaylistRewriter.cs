using System.Text;

namespace Blocwerk.Core.Services;

/// <summary>
/// Rewrites the URI lines of an HLS playlist so an anonymous share viewer's follow-up requests stay
/// authorized. A share viewer reaches the ladder with <c>?token=…</c>; hls.js (and native Safari) then
/// request the child variant playlists and segments named in the playlist WITHOUT that token unless it
/// is carried on each URI. Cookie-authorized viewers need no rewrite — the browser attaches the cookie
/// itself — so this is only applied on the token path. Pure text transform, unit-tested directly.
/// </summary>
public static class HlsPlaylistRewriter
{
    /// <summary>
    /// Appends <c>?token=&lt;token&gt;</c> (url-encoded) to every non-comment, non-blank line — the child
    /// variant-playlist and segment URIs — and leaves <c>#</c> tag lines and blank lines untouched. A URI
    /// that already carries a query gets the token with <c>&amp;</c>. Line endings are preserved as
    /// <c>\n</c>. An empty/whitespace token returns the playlist unchanged.
    /// </summary>
    public static string AppendToken(string playlist, string token)
    {
        if (string.IsNullOrEmpty(playlist) || string.IsNullOrWhiteSpace(token))
        {
            return playlist;
        }

        var encoded = Uri.EscapeDataString(token);
        var lines = playlist.Split('\n');
        var sb = new StringBuilder(playlist.Length + lines.Length * (encoded.Length + 8));

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimEnd('\r');

            // Blank lines are structural. Most #-tags are too, but a few carry a URI="…" attribute
            // (#EXT-X-MAP init segment, #EXT-X-MEDIA alternate rendition, #EXT-X-KEY) that a share
            // viewer will fetch — those need the token inside the quoted URI. Every other tag is left
            // verbatim.
            if (trimmed.Length == 0)
            {
                sb.Append(line);
            }
            else if (trimmed[0] == '#')
            {
                sb.Append(RewriteTagUri(trimmed, encoded));
                if (line.EndsWith('\r'))
                {
                    sb.Append('\r');
                }
            }
            else
            {
                sb.Append(trimmed).Append(AppendSeparator(trimmed)).Append("token=").Append(encoded);
                if (line.EndsWith('\r'))
                {
                    sb.Append('\r');
                }
            }

            if (i < lines.Length - 1)
            {
                sb.Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>Tags whose <c>URI="…"</c> attribute points at a fetched resource and so needs the token.</summary>
    private static readonly string[] UriBearingTags = ["#EXT-X-MAP", "#EXT-X-MEDIA", "#EXT-X-KEY"];

    /// <summary>
    /// Appends the token inside the quoted <c>URI="…"</c> of a URI-bearing tag, leaving every other tag
    /// (and any tag without such an attribute) untouched. Only the first <c>URI="…"</c> is rewritten —
    /// these tags carry at most one.
    /// </summary>
    private static string RewriteTagUri(string tagLine, string encodedToken)
    {
        if (!IsUriBearingTag(tagLine))
        {
            return tagLine;
        }

        const string marker = "URI=\"";
        var start = tagLine.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0)
        {
            return tagLine;
        }

        var valueStart = start + marker.Length;
        var valueEnd = tagLine.IndexOf('"', valueStart);
        if (valueEnd < 0)
        {
            return tagLine;
        }

        var uri = tagLine[valueStart..valueEnd];
        var rewritten = uri + AppendSeparator(uri) + "token=" + encodedToken;
        return string.Concat(tagLine.AsSpan(0, valueStart), rewritten, tagLine.AsSpan(valueEnd));
    }

    private static bool IsUriBearingTag(string tagLine)
    {
        foreach (var tag in UriBearingTags)
        {
            if (tagLine.StartsWith(tag, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static char AppendSeparator(string uri) => uri.Contains('?') ? '&' : '?';
}
