// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Assets;

/// <summary>A stored-file column of an asset row and the column holding the file's <see cref="ContentHash"/>.</summary>
/// <param name="Entity">The entity type.</param>
/// <param name="Path">The stored-name property.</param>
/// <param name="Hash">The <c>byte[]</c> property that holds the hash of the file <paramref name="Path"/> names.</param>
/// <param name="InCaptureStore">Whether the file lives in the capture store (else the wall-image store).</param>
public sealed record ContentHashFileColumn(Type Entity, string Path, string Hash, bool InCaptureStore)
{
    /// <summary>
    /// Every stored file that is served as an asset and hashed by its writer; the backfill hashes the rows that predate
    /// it. The guard test reads this list, so a new hashed file column goes here.
    /// </summary>
    public static IReadOnlyList<ContentHashFileColumn> Stored { get; } =
    [
        new(typeof(WallImage), nameof(WallImage.StoragePath), nameof(WallImage.Sha256), InCaptureStore: false),
        new(typeof(WallGeometrySplat), nameof(WallGeometrySplat.StoredPath), nameof(WallGeometrySplat.Sha256), InCaptureStore: true),
        new(typeof(WallGeometrySplat), nameof(WallGeometrySplat.MobileStoredPath), nameof(WallGeometrySplat.MobileSha256), InCaptureStore: true),
        new(typeof(WallGeometryTexture), nameof(WallGeometryTexture.StoredPath), nameof(WallGeometryTexture.Sha256), InCaptureStore: true),
        new(typeof(WallGeometryTexture), nameof(WallGeometryTexture.MaskStoredPath), nameof(WallGeometryTexture.MaskSha256), InCaptureStore: true),
    ];
}
