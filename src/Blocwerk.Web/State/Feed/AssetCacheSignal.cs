// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.State.Feed;

/// <summary>What a feed delivery asks of the page's device cache (<see cref="AssetCacheSignals"/>).</summary>
public enum AssetCacheSignal
{
    /// <summary>Nothing the device keeps changed (ticks, comments, videos: leaf rows no cached document reads yet).</summary>
    None,

    /// <summary>A data document's input changed: refresh the wall's docs index (<c>bwAssetData.changed</c>).</summary>
    Data,

    /// <summary>A <c>wall.manifest</c>, a <c>wall.resync</c> or a resync: refetch the manifest, then the docs index.</summary>
    Manifest,
}
