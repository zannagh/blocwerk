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
