// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>
/// Whether a cached manifest still holds: the wall's feed rows after the entry's cursor are read (every audience, since
/// the check is the server's own), and any row that can change a manifest input means a rebuild. "Can change" is
/// decided by exclusion: only the kinds listed in <see cref="Unrelated"/> are skipped, so a kind added later rebuilds
/// until somebody decides otherwise.
/// </summary>
/// <remarks>
/// Reading the rows, not a per-wall eviction signal, is what makes a missed event impossible: the cursor comes from
/// below the horizon, so a slow transaction committing behind it still shows up in the next read (design §5.2, B1).
/// </remarks>
public static class ManifestFreshness
{
    /// <summary>How many rows one check reads; a busier gap than this rebuilds.</summary>
    public const int PageSize = 100;

    /// <summary>
    /// Gets the kinds that never touch a manifest input: holds, structure without photos, membership and access (the
    /// classes do not depend on who is a member), and the social and per-user rows. <c>boulder.changed</c> is not here:
    /// a draft flip moves the boulder's thumbnails in or out of the share class.
    /// </summary>
    public static IReadOnlySet<string> Unrelated { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        FeedKinds.MemberChanged, FeedKinds.AccessRevoked, FeedKinds.SegmentChanged, FeedKinds.LinkChanged, FeedKinds.VolumeChanged,
        FeedKinds.HoldChanged, FeedKinds.HoldDeleted, FeedKinds.BoulderStats, FeedKinds.CommentAdded, FeedKinds.CommentDeleted,
        FeedKinds.ActivityAdded, FeedKinds.FavoriteChanged, FeedKinds.RatingMine, FeedKinds.TickMine, FeedKinds.UserProfile,
        FeedKinds.TicksResync,
    };

    /// <summary>The cursor the entry may move up to when it still holds, or null when it must be rebuilt.</summary>
    public static async Task<FeedCursor?> CheckAsync(IWallFeed feed, Guid wallId, FeedCursor cursor, CancellationToken ct)
    {
        var page = await feed.ReadAsync(FeedFilter.AllOfWall(wallId), cursor, PageSize, ct);
        if (page.Resync || page.Changes.Count >= PageSize || page.Changes.Any(c => !Unrelated.Contains(c.Kind)))
        {
            return null;
        }

        return page.Next;
    }
}
