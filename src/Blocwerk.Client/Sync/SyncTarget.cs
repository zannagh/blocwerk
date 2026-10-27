// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Client.Sync;

/// <summary>
/// The wall a client URL shows (<c>/v/walls/{w}…</c>, the Server routes' twins) and a share link's token
/// (<c>/shared/{t}</c> or <c>?token=</c>, as asset-wipe.js <c>tokenOf</c> reads it). Parsed by hand: no regex engine
/// joins the client's bundle for it.
/// </summary>
/// <param name="WallId">The wall, or null when the URL shows none.</param>
/// <param name="Token">The share token, or null.</param>
public sealed record SyncTarget(Guid? WallId, string? Token)
{
    /// <summary>Gets no wall.</summary>
    public static SyncTarget None { get; } = new(null, null);

    /// <summary>The wall and token of a client URL.</summary>
    /// <param name="uri">An absolute URL, or a path.</param>
    /// <returns>The target.</returns>
    public static SyncTarget Of(string uri)
    {
        var rest = Uri.TryCreate(uri, UriKind.Absolute, out var absolute) ? absolute.PathAndQuery : uri;
        var cut = rest.IndexOfAny(['?', '#']);
        var path = cut < 0 ? rest : rest[..cut];
        var query = cut >= 0 && rest[cut] == '?' ? rest[(cut + 1)..].Split('#')[0] : string.Empty;
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 3 || segments[0] != "v" || segments[1] != "walls"
            || !Guid.TryParseExact(segments[2], "D", out var wallId))
        {
            return None;
        }

        var shared = Array.IndexOf(segments, "shared", 3);
        var token = shared > 0 && shared + 1 < segments.Length ? Uri.UnescapeDataString(segments[shared + 1]) : QueryToken(query);
        return new SyncTarget(wallId, string.IsNullOrEmpty(token) ? null : token);
    }

    private static string? QueryToken(string query)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var at = pair.IndexOf('=', StringComparison.Ordinal);
            if (at > 0 && pair[..at] == "token")
            {
                return Uri.UnescapeDataString(pair[(at + 1)..].Replace('+', ' '));
            }
        }

        return null;
    }
}
