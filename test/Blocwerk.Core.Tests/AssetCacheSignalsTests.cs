// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Feed;
using Blocwerk.Web.State.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="AssetCacheSignals"/> (Phase 3 PR 3.3): which circuit feed deliveries make the page's device cache refetch
/// the manifest or the docs index (<c>bwAssetData.changed</c>), which the viewer's own marks (<c>bwAssetMine.changed</c>,
/// PR 3.4), and which reach it not at all.
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
    [InlineData(FeedKinds.CommentAdded)]
    [InlineData(FeedKinds.CommentDeleted)]
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

    [Theory]
    [InlineData(FeedKinds.TickMine)]
    [InlineData(FeedKinds.FavoriteChanged)]
    public void AnOwnMark_RefetchesMine_AndNoDocument(string kind)
    {
        Assert.True(AssetCacheSignals.ChangesMine(Delivery(FeedKinds.VideoStatus, kind)));
        Assert.Equal(AssetCacheSignal.None, AssetCacheSignals.Of(Delivery(kind)));
    }

    /// <summary>A deleted, drafted or archived boulder takes its marks out of the reader's set: mine and the docs both.</summary>
    [Theory]
    [InlineData(FeedKinds.BoulderChanged)]
    [InlineData(FeedKinds.BoulderDeleted)]
    public void ABoulderChange_RefetchesMine_AndTheDocs(string kind)
    {
        Assert.True(AssetCacheSignals.ChangesMine(Delivery(kind)));
        Assert.Equal(AssetCacheSignal.Data, AssetCacheSignals.Of(Delivery(kind)));
    }

    /// <summary>Own ratings are not offline in v1 (plan §2), and no other wall-wide kind moves the viewer's own marks.</summary>
    [Theory]
    [InlineData(FeedKinds.RatingMine)]
    [InlineData(FeedKinds.BoulderStats)]
    [InlineData(FeedKinds.HoldChanged)]
    [InlineData(FeedKinds.TicksResync)]
    public void AnythingElse_LeavesMineAlone(string kind) =>
        Assert.False(AssetCacheSignals.ChangesMine(Delivery(kind)));

    private static FeedDelivery Delivery(params string[] kinds) =>
        new(Guid.NewGuid(), [.. kinds.Select(kind => new FeedEnvelope { Cursor = Cursor.ToString(), Kind = kind })], Cursor, Resync: false);
}
