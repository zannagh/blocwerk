// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The catch-up read (design §5.8): one horizon read of the rows a viewer may see after a client's cursor, hydrated
/// exactly as the hub hydrates its deliveries (<see cref="FeedHydration"/>, <see cref="AuthorVisibility"/>). Access is
/// decided before this runs (<see cref="FeedAccessGate"/>); this only narrows the rows to the viewer's audiences.
/// </summary>
/// <remarks>
/// A missing or unparseable cursor is answered like one of another epoch: a resync whose <c>next</c> is the high-water
/// mark, so a client that reloads and continues from there misses nothing. A singleton over the session-less factory.
/// </remarks>
public sealed class FeedCatchUp(IWallFeed feed, Func<BlocwerkDbContext> createContext)
{
    /// <summary>The page size when the client names none.</summary>
    public const int DefaultLimit = 500;

    // Never an epoch: a cursor that does not parse resyncs through the same path as one of another epoch.
    private static readonly FeedCursor Unknown = new(Guid.Empty, FeedPosition.Start);

    /// <summary>One wall's rows for <paramref name="viewer"/>: its audiences, plus its own User rows of that wall.</summary>
    public Task<FeedChanges> ReadWallAsync(
        Guid wallId, FeedViewer viewer, string? after, int? limit, CancellationToken cancellationToken = default) =>
        ReadAsync(WallFilter(wallId, viewer), viewer, after, limit, cancellationToken);

    /// <summary>
    /// The cursor a reload of <paramref name="wallId"/> continues from: the global high-water mark for a member, the
    /// wall's own mark (<see cref="FeedScopedMark"/>) for an outsider.
    /// </summary>
    public async Task<FeedCursor> MarkAsync(Guid wallId, FeedViewer viewer, CancellationToken cancellationToken = default) =>
        await ScopeAsync(WallFilter(wallId, viewer), viewer, await feed.CurrentAsync(cancellationToken), cancellationToken);

    /// <summary>A user's own rows across every wall (<c>access.revoked</c>, mine rows, profile, the resyncs).</summary>
    public Task<FeedChanges> ReadSelfAsync(Guid userId, string? after, int? limit, CancellationToken cancellationToken = default) =>
        ReadAsync(FeedFilter.ForUser(userId), FeedViewer.Member(userId), after, limit, cancellationToken);

    /// <summary>The parsed cursor, or one that resyncs.</summary>
    public static FeedCursor Parse(string? after) => FeedCursor.TryParse(after, out var cursor) ? cursor : Unknown;

    /// <summary>
    /// Hydrates a page's rows as <paramref name="viewer"/> sees them, in one batch per kind of content, without the kiosk's
    /// activity rows it may not read now (<see cref="FeedKioskActivity"/>).
    /// </summary>
    public async Task<IReadOnlyList<FeedEnvelope>> HydrateAsync(FeedPage page, FeedViewer viewer, CancellationToken cancellationToken)
    {
        if (page.Changes.Count == 0)
        {
            return [];
        }

        await using var db = createContext();
        var hydrated = await FeedHydration.LoadAsync(db, page.Changes, cancellationToken);
        var visibility = AuthorVisibility.For(viewer);
        return page.Changes.Where(r => FeedKioskActivity.Allows(viewer, r, hydrated))
            .Select(r => FeedEnvelopes.Build(r, page.Next.Epoch, hydrated, visibility))
            .ToArray();
    }

    private async Task<FeedChanges> ReadAsync(
        FeedFilter filter, FeedViewer viewer, string? after, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? DefaultLimit, 1, WallFeed.MaxLimit);
        var page = await feed.ReadAsync(filter, Parse(after), size, cancellationToken);
        var events = await HydrateAsync(page, viewer, cancellationToken);
        var more = !page.Resync && page.Changes.Count == size;

        // A full page ends on a row of the filter; a short one or a resync ends on the global mark, which outsiders do not get.
        var next = more ? page.Next : await ScopeAsync(filter, viewer, page.Next, cancellationToken);
        return new FeedChanges(events, next.ToString(), page.Resync, more);
    }

    private static FeedFilter WallFilter(Guid wallId, FeedViewer viewer) =>
        FeedFilter.ForViewer(wallId, viewer);

    // Members see the wall's member list and every author id anyway; the global mark tells them nothing they may not know.
    private async Task<FeedCursor> ScopeAsync(FeedFilter filter, FeedViewer viewer, FeedCursor global, CancellationToken cancellationToken)
    {
        if (viewer.Audiences.Contains(FeedAudience.Members))
        {
            return global;
        }

        await using var db = createContext();
        return await FeedScopedMark.OfAsync(db, filter, global, cancellationToken);
    }
}
