// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Text.RegularExpressions;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Assets;

/// <summary>
/// The one place that builds the URLs of a wall's stored assets. With the row's content hash it is the
/// content-addressed <c>/api/walls/{w}/a/{sha}</c> (revalidated through the access gate by the HTTP cache, kept
/// cache-first by the service worker); without one it is the legacy route with whatever version that route already
/// carries (<c>?rev=</c>, <c>?v=</c>), so a row not yet backfilled keeps working exactly as before (dual-read).
/// </summary>
/// <remarks>
/// <para>
/// Every sha handed to this class must come from the DATABASE, read with the rest of what the page renders
/// (<see cref="WallPanelInfo.PhotoSha"/>, <see cref="IAssetHashReader"/>, the gallery and 3D projections). Never
/// from a circuit's cached wall: that can hold the null hashes it was loaded with before the backfill, and a sha
/// that is no longer any row's is a 404 where the legacy URL would still have worked. When in doubt, pass null.
/// </para>
/// <para>
/// A malformed sha (not 64 lowercase hex characters) counts as none: the route would 404 on it.
/// </para>
/// </remarks>
public static partial class AssetUrls
{
    /// <summary>
    /// The content-addressed URL of <paramref name="sha"/> on <paramref name="wallId"/>, with the share token when
    /// there is one; null when there is no usable sha.
    /// </summary>
    public static string? Hashed(Guid wallId, string? sha, string? shareToken = null) =>
        IsSha(sha) ? WithToken($"/api/walls/{wallId}/a/{sha}", shareToken) : null;

    /// <summary>The hashed URL when <paramref name="sha"/> is usable, else <paramref name="legacy"/> unchanged.</summary>
    public static string HashedOr(Guid wallId, string? sha, string? shareToken, string legacy) =>
        Hashed(wallId, sha, shareToken) ?? legacy;

    /// <summary>A panel's live photo: hashed, or the legacy panel route with the token and the crop revision.</summary>
    public static string Panel(Guid wallId, WallPanelInfo panel, string? shareToken = null) =>
        Hashed(wallId, panel.PhotoSha, shareToken)
            ?? panel.WithPhotoRevision(WithToken($"/api/walls/{wallId}/panels/{panel.Id}/photo", shareToken));

    /// <summary>A panel's live photo by id, when only its id and (maybe) its hash are at hand.</summary>
    public static string Panel(Guid wallId, Guid panelId, string? sha, string? shareToken = null) =>
        Hashed(wallId, sha, shareToken) ?? WithToken($"/api/walls/{wallId}/panels/{panelId}/photo", shareToken);

    /// <summary>
    /// The wall's photo: the live one (<c>/photo</c>, the centre panel) or, with a <paramref name="generation"/>, the
    /// photo as it was then (<c>/photo/{generation}</c>). <paramref name="sha"/> must be of that same photo.
    /// </summary>
    public static string WallPhoto(Guid wallId, string? sha, string? shareToken = null, int? generation = null) =>
        Hashed(wallId, sha, shareToken)
            ?? WithToken(generation is { } g ? $"/api/walls/{wallId}/photo/{g}" : $"/api/walls/{wallId}/photo", shareToken);

    /// <summary>A beta video's poster frame: hashed on its wall, or the legacy thumbnail route.</summary>
    public static string VideoThumbnail(Guid wallId, Guid videoId, string? sha, string? shareToken = null) =>
        Hashed(wallId, sha, shareToken) ?? WithToken($"/api/beta-videos/{videoId}/thumbnail", shareToken);

    /// <summary>Whether <paramref name="url"/> is a content-addressed asset URL (with or without a width).</summary>
    public static bool IsHashed(string? url) => url is not null && HashedPath().IsMatch(PathOf(url));

    /// <summary>
    /// A hashed <paramref name="url"/> asking for the <paramref name="width"/> rendition: the width is a path segment
    /// (<c>/a/{sha}/{width}</c>), replacing any width already there; the query (share token) is kept.
    /// </summary>
    public static string HashedAtWidth(string url, int width)
    {
        var (path, query) = Split(url);
        var match = HashedPath().Match(path);
        return match.Success
            ? match.Groups["base"].Value + "/" + width.ToString(CultureInfo.InvariantCulture) + query
            : url;
    }

    /// <summary>Whether <paramref name="sha"/> is in the one form a hashed URL takes: 64 lowercase hex characters.</summary>
    public static bool IsSha(string? sha) =>
        sha is { Length: ContentHash.Length * 2 } && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string WithToken(string url, string? shareToken) =>
        string.IsNullOrEmpty(shareToken) ? url : url + "?token=" + Uri.EscapeDataString(shareToken);

    private static string PathOf(string url) => Split(url).Path;

    private static (string Path, string Query) Split(string url)
    {
        var at = url.IndexOf('?');
        return at < 0 ? (url, string.Empty) : (url[..at], url[at..]);
    }

    [GeneratedRegex("^(?<base>/api/walls/[0-9a-fA-F-]{36}/a/[0-9a-f]{64})(?:/[0-9]+)?$")]
    private static partial Regex HashedPath();
}
