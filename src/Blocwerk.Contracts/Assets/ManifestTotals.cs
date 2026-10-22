// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Assets;

/// <summary>
/// The bytes of the listed originals per tier, for ordering the fill and choosing what to evict; never a reason to skip a
/// wall. A client fills <c>core</c> with renditions, which are smaller than the originals, so <see cref="Core"/> is an
/// upper bound of what the background fill downloads; of <see cref="View3d"/> it keeps one splat level only.
/// </summary>
/// <param name="Core">Current-generation panels (and the origin wall photo).</param>
/// <param name="Lazy">Historic panels, resets, gallery and video thumbnails, cached on use.</param>
/// <param name="View3d">Textures, masks and every listed splat level: the 3D data a user may opt out of.</param>
public sealed record ManifestTotals(long Core, long Lazy, long View3d);
