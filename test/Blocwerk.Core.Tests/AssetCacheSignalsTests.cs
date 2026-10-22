// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;
using Blocwerk.Web.State.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="AssetCacheSignals"/> (Phase 3 PR 3.3): which circuit feed deliveries make the page's device cache refetch
/// the manifest or the docs index (<c>bwAssetData.changed</c>), and which reach it not at all.
/// </summary>
public sealed class AssetCacheSignalsTests
{
    private static readonly FeedCursor Cursor = new(Guid.NewGuid(), new FeedPosition(null, 1));

    [Theory]
    [InlineData(FeedKinds.WallManifest)]
    [InlineData(FeedKinds.WallResync)]
    public void AManifestOrWallResync_RefetchesTheManifest(string kind) =>
        Assert.Equal(AssetCacheSignal.Manifest, AssetCacheSignals.Of(Delivery(FeedKinds.HoldChanged, kind)));

    [Fact]
    public void AResync_RefetchesTheManifest() =>
        Assert.Equal(AssetCacheSignal.Manifest, AssetCacheSignals.Of(new FeedDelivery(Guid.NewGuid(), [], Cursor, Resync: true)));

    [Theory]
    [InlineData(FeedKinds.WallUpdated)]
    [InlineData(FeedKinds.SegmentChanged)]
    [InlineData(FeedKinds.PanelChanged)]
    [InlineData(FeedKinds.HoldChanged)]
    [InlineData(FeedKinds.HoldDeleted)]
    [InlineData(FeedKinds.BoulderChanged)]
    [InlineData(FeedKinds.BoulderDeleted)]
    [InlineData(FeedKinds.BoulderStats)]
    [InlineData(FeedKinds.UserProfile)]
    [InlineData(FeedKinds.GalleryChanged)]
    [InlineData(FeedKinds.GalleryDeleted)]
    public void ADocumentInput_RefreshesTheDocsIndex(string kind) =>
        Assert.Equal(AssetCacheSignal.Data, AssetCacheSignals.Of(Delivery(kind)));

    [Theory]
    [InlineData(FeedKinds.TickMine)]
    [InlineData(FeedKinds.FavoriteChanged)]
    [InlineData(FeedKinds.VideoStatus)]
    [InlineData(FeedKinds.PanelStaged)]
    [InlineData(FeedKinds.ActivityAdded)]
    [InlineData("some.future.kind")]
    public void AKindNoDocumentReads_ReachesNothing(string kind) =>
        Assert.Equal(AssetCacheSignal.None, AssetCacheSignals.Of(Delivery(kind)));

    private static FeedDelivery Delivery(params string[] kinds) =>
        new(Guid.NewGuid(), [.. kinds.Select(kind => new FeedEnvelope { Cursor = Cursor.ToString(), Kind = kind })], Cursor, Resync: false);
}
