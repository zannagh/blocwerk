// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Assets;

/// <summary>
/// The bytes of the listed originals per tier. A client fills <c>core</c> with renditions, which are smaller than the
/// originals, so <see cref="Core"/> is an upper bound of what the background fill downloads.
/// </summary>
/// <param name="Core">Current-generation panels (and the origin wall photo).</param>
/// <param name="Lazy">Historic panels, resets, gallery and video thumbnails, cached on use.</param>
/// <param name="View3d">Textures and masks, cached when the 3D view opens.</param>
/// <param name="Optin">Splat files, cached only with "Save 3D offline".</param>
public sealed record ManifestTotals(long Core, long Lazy, long View3d, long Optin);
