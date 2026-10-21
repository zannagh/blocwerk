// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;

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
        ReadAsync(FeedFilter.ForWall(wallId, viewer.Audiences.ToArray(), viewer.UserId), viewer, after, limit, cancellationToken);

    /// <summary>A user's own rows across every wall (<c>access.revoked</c>, mine rows, profile, the resyncs).</summary>
    public Task<FeedChanges> ReadSelfAsync(Guid userId, string? after, int? limit, CancellationToken cancellationToken = default) =>
        ReadAsync(FeedFilter.ForUser(userId), FeedViewer.Member(userId), after, limit, cancellationToken);

    /// <summary>The parsed cursor, or one that resyncs.</summary>
    public static FeedCursor Parse(string? after) => FeedCursor.TryParse(after, out var cursor) ? cursor : Unknown;

    /// <summary>Hydrates a page's rows as <paramref name="viewer"/> sees them, in one batch per kind of content.</summary>
    public async Task<IReadOnlyList<FeedEnvelope>> HydrateAsync(FeedPage page, FeedViewer viewer, CancellationToken cancellationToken)
    {
        if (page.Changes.Count == 0)
        {
            return [];
        }

        await using var db = createContext();
        var hydrated = await FeedHydration.LoadAsync(db, page.Changes, cancellationToken);
        var visibility = AuthorVisibility.For(viewer);
        return page.Changes.Select(r => FeedEnvelopes.Build(r, page.Next.Epoch, hydrated, visibility)).ToArray();
    }

    private async Task<FeedChanges> ReadAsync(
        FeedFilter filter, FeedViewer viewer, string? after, int? limit, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(limit ?? DefaultLimit, 1, WallFeed.MaxLimit);
        var page = await feed.ReadAsync(filter, Parse(after), size, cancellationToken);
        var events = await HydrateAsync(page, viewer, cancellationToken);
        return new FeedChanges(events, page.Next.ToString(), page.Resync, More: !page.Resync && page.Changes.Count == size);
    }
}
