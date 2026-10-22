// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Feed;

namespace Blocwerk.Web.State.Feed;

/// <summary>
/// Maps a circuit's feed delivery to what the page's device cache should refetch (Phase 3 PR 3.3): the manifest on a
/// <c>wall.manifest</c> or any resync, the docs index on an input kind of any data document
/// (<see cref="DataDocumentInputs"/>, so a kind added there is followed here without a change), and the viewer's own
/// marks on a <c>tick.mine</c> or <c>favorite.changed</c> (PR 3.4; User-audience rows, so only the ticker's own circuits
/// ever see them, never a kiosk's).
/// </summary>
public static class AssetCacheSignals
{
    private static readonly HashSet<string> DataKinds =
        DataDocumentKinds.All.SelectMany(DataDocumentInputs.Of).ToHashSet(StringComparer.Ordinal);

    private static readonly HashSet<string> MineKinds = new(StringComparer.Ordinal) { FeedKinds.TickMine, FeedKinds.FavoriteChanged };

    /// <summary>The strongest signal of the delivery's events.</summary>
    public static AssetCacheSignal Of(FeedDelivery delivery)
    {
        if (delivery.Resync || delivery.Events.Any(e => e.Kind is FeedKinds.WallManifest or FeedKinds.WallResync))
        {
            return AssetCacheSignal.Manifest;
        }

        return delivery.Events.Any(e => DataKinds.Contains(e.Kind)) ? AssetCacheSignal.Data : AssetCacheSignal.None;
    }

    /// <summary>True when the delivery carries a change of the viewer's own marks (<c>bwAssetMine.changed</c>).</summary>
    public static bool ChangesMine(FeedDelivery delivery) => delivery.Events.Any(e => MineKinds.Contains(e.Kind));
}
