// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>One wall's share of the cache. An asset two walls list counts in both walls' sizes.</summary>
/// <param name="WallId">The wall's id, lower-case.</param>
/// <param name="Core">The current panels at this device's fit width and 640.</param>
/// <param name="Gallery">Gallery images.</param>
/// <param name="View3d">3D textures, masks and splats.</param>
/// <param name="Other">Everything cached on use: superseded panels, resets, larger renditions, video thumbnails.</param>
/// <param name="Total">The sum of the four.</param>
/// <param name="KeepOffline">Pinned: never evicted to make room for another wall.</param>
/// <param name="Ready">Every core asset of the wall is on the device (the offline-ready mark).</param>
/// <param name="LastOpened">When the wall was last opened here, in Unix milliseconds (0 = never).</param>
/// <param name="Saved3d">"Save 3D offline" (PR 2.8): pinned, and its 3D filled on every device and connection.</param>
public sealed record OfflineWallUsage(
    string WallId,
    long Core,
    long Gallery,
    long View3d,
    long Other,
    long Total,
    bool KeepOffline,
    bool Ready,
    long LastOpened,
    bool Saved3d = false);
