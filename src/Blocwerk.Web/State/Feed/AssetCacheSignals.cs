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
/// (<see cref="DataDocumentInputs"/>, so a kind added there is followed here without a change).
/// </summary>
public static class AssetCacheSignals
{
    private static readonly HashSet<string> DataKinds =
        DataDocumentKinds.All.SelectMany(DataDocumentInputs.Of).ToHashSet(StringComparer.Ordinal);

    /// <summary>The strongest signal of the delivery's events.</summary>
    public static AssetCacheSignal Of(FeedDelivery delivery)
    {
        if (delivery.Resync || delivery.Events.Any(e => e.Kind is FeedKinds.WallManifest or FeedKinds.WallResync))
        {
            return AssetCacheSignal.Manifest;
        }

        return delivery.Events.Any(e => DataKinds.Contains(e.Kind)) ? AssetCacheSignal.Data : AssetCacheSignal.None;
    }
}
