// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;

namespace Blocwerk.Client.Views;

/// <summary>
/// The content-addressed photo URLs the views load (<c>/api/walls/{w}/a/{sha}/{width}</c>, as the Server's
/// <c>AssetUrls</c> builds them). The worker answers them from the device cache under the active partition, keyed by
/// sha and width only, so the share token rides along just for a miss that goes to the network.
/// </summary>
public static class PhotoUrls
{
    /// <summary>
    /// The renditions to try, in order: the one the Server pages cached for this screen, the thumbnail they cache for
    /// every core photo, then the original (online only). None for a photo without a usable sha.
    /// </summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="sha">The photo's sha.</param>
    /// <param name="shareToken">The share link's token, or null.</param>
    /// <param name="fitWidth">The device's rung (<see cref="ClientViewFacts.FitWidth"/>).</param>
    /// <returns>The URLs, distinct.</returns>
    public static IReadOnlyList<string> Renditions(Guid wallId, string? sha, string? shareToken, int fitWidth)
    {
        if (!IsSha(sha))
        {
            return [];
        }

        var path = $"/api/walls/{wallId}/a/{sha}";
        return new[] { At(path, fitWidth), At(path, ClientViewFacts.ThumbnailWidth), path }
            .Distinct(StringComparer.Ordinal)
            .Select(url => WithToken(url, shareToken))
            .ToList();
    }

    /// <summary>The thumbnail rendition alone (a wall card), or null.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="sha">The photo's sha.</param>
    /// <param name="shareToken">The share link's token, or null.</param>
    /// <returns>The URL, or null.</returns>
    public static string? Thumbnail(Guid wallId, string? sha, string? shareToken) =>
        IsSha(sha) ? WithToken(At($"/api/walls/{wallId}/a/{sha}", ClientViewFacts.ThumbnailWidth), shareToken) : null;

    /// <summary>Whether <paramref name="sha"/> is 64 lowercase hex digits, the one form a hashed URL takes.</summary>
    /// <param name="sha">The sha.</param>
    /// <returns>Whether it is usable.</returns>
    public static bool IsSha(string? sha) => sha is { Length: 64 } && sha.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static string At(string path, int width) => path + "/" + width.ToString(CultureInfo.InvariantCulture);

    private static string WithToken(string url, string? shareToken) =>
        string.IsNullOrEmpty(shareToken) ? url : url + "?token=" + Uri.EscapeDataString(shareToken);
}
