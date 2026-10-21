// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// What the wall-asset cache holds on this device, as <c>bwAssetReport.report()</c> (wwwroot/js/asset-report.js)
/// answers it. Every byte count is the page ledger's own, never <c>navigator.storage.estimate()</c>, which is
/// bogus on iOS.
/// </summary>
/// <param name="Caching">Whether this page caches at all (a real partition label and a started cache).</param>
/// <param name="Kiosk">Whether the partition is a wall tablet's.</param>
/// <param name="Persisted">Whether the browser granted persistent storage (it will not evict on its own).</param>
/// <param name="Keep3d">The 3D opt-out cookie <c>blocwerk-cache-3d</c>: true unless turned off.</param>
/// <param name="Mobile">A phone or tablet: 3D is cached only when a 3D view is opened.</param>
/// <param name="Background3d">Whether this device downloads 3D in the background right now.</param>
/// <param name="IosTab">An iPhone or iPad Safari tab, where storage is best-effort; the installed app keeps it.</param>
/// <param name="InstallHint">How to install on this platform (<c>bwInstall.instructions()</c>), in an iOS tab only.</param>
/// <param name="Paused">The browser refused storage on this page, so walls stream from the network.</param>
/// <param name="Total">Bytes held in this partition; an asset two walls share counts once.</param>
/// <param name="Walls">Per wall, most recently opened first.</param>
public sealed record OfflineStorageFacts(
    bool Caching,
    bool Kiosk,
    bool Persisted,
    bool Keep3d,
    bool Mobile,
    bool Background3d,
    bool IosTab,
    string? InstallHint,
    bool Paused,
    long Total,
    IReadOnlyList<OfflineWallUsage>? Walls);

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
