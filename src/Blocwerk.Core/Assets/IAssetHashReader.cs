// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Assets;

/// <summary>
/// The content hashes behind the asset URLs a page renders, read from the DATABASE on each call (never from a circuit's
/// cached wall, which can hold pre-backfill nulls). Each one names exactly the bytes the matching legacy route would
/// serve; when that is not certain the answer is null, so <see cref="AssetUrls"/> falls back to the legacy URL rather
/// than hand out a sha that is a 404.
/// </summary>
/// <remarks>
/// A hash is not a capability: the <c>/a/</c> route gates every request on its own, so these reads only decide which
/// URL to build. All values are lowercase hex.
/// </remarks>
public interface IAssetHashReader
{
    /// <summary>
    /// The hash of what <c>/api/walls/{w}/photo</c> serves: the newest live centre panel's photo, or the retiring
    /// <c>Wall.Photo</c> when the wall has no live centre panel.
    /// </summary>
    Task<string?> WallPhotoAsync(Guid wallId, CancellationToken ct = default);

    /// <summary><see cref="WallPhotoAsync"/> for many walls at once (a wall list); walls without one are left out.</summary>
    Task<IReadOnlyDictionary<Guid, string>> WallPhotosAsync(IReadOnlyCollection<Guid> wallIds, CancellationToken ct = default);

    /// <summary>The hash of what <c>/api/walls/{w}/photo/{generation}</c> serves to this caller.</summary>
    Task<string?> WallPhotoAtGenerationAsync(Guid wallId, string? shareToken, int generation);

    /// <summary>The live photo hash of every hashed panel row of the wall, by panel id.</summary>
    Task<IReadOnlyDictionary<Guid, string>> PanelPhotosAsync(Guid wallId, CancellationToken ct = default);
}
