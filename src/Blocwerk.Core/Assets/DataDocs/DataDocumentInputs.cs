// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;

namespace Blocwerk.Core.Assets.DataDocs;

/// <summary>
/// The feed kinds that can change each document: a stored document is rebuilt once the wall has a row of one of its
/// kinds after the document's build mark (<see cref="DataDocumentFreshness"/>). <c>wall.resync</c> is in every list: it
/// stands for writes too large or too unknown to map (a scope, an erasure), so it rebuilds everything.
/// </summary>
public static class DataDocumentInputs
{
    private static readonly string[] Wall =
    [
        FeedKinds.WallUpdated, FeedKinds.WallResync, FeedKinds.WallManifest, FeedKinds.SegmentChanged, FeedKinds.PanelChanged,
    ];

    private static readonly string[] Holds =
    [
        FeedKinds.HoldChanged, FeedKinds.HoldDeleted, FeedKinds.PanelChanged, FeedKinds.WallUpdated, FeedKinds.WallResync,
    ];

    // boulder.stats: the counts are in the document; user.profile: the byline is (a setter renamed); hold.deleted: a
    // boulder's hold refs; wall.updated: a generation bump retires boulders.
    private static readonly string[] Boulders =
    [
        FeedKinds.BoulderChanged, FeedKinds.BoulderDeleted, FeedKinds.BoulderStats, FeedKinds.UserProfile, FeedKinds.HoldDeleted,
        FeedKinds.WallUpdated, FeedKinds.WallResync,
    ];

    // wall.manifest: the hash backfill writes a gallery sha without a gallery.changed.
    private static readonly string[] Gallery =
    [
        FeedKinds.GalleryChanged, FeedKinds.GalleryDeleted, FeedKinds.WallManifest, FeedKinds.WallResync,
    ];

    /// <summary>The input kinds of <paramref name="kind"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Not a data document kind.</exception>
    public static IReadOnlyList<string> Of(string kind) => kind switch
    {
        DataDocumentKinds.Wall => Wall,
        DataDocumentKinds.Holds => Holds,
        DataDocumentKinds.Boulders => Boulders,
        DataDocumentKinds.Gallery => Gallery,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not a data document kind."),
    };
}
