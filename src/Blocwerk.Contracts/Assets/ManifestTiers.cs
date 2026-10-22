// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Assets;

/// <summary>
/// The tiers of <see cref="ManifestAsset.Tier"/>, in fill order (design §6.8). Wall data (<see cref="Core"/>,
/// <see cref="Lazy"/>) is always cached, with no setting; only <see cref="View3d"/> can be switched off.
/// </summary>
public static class ManifestTiers
{
    /// <summary>Filled in the background.</summary>
    public const string Core = "core";

    /// <summary>Cached when used.</summary>
    public const string Lazy = "lazy";

    /// <summary>
    /// The 3D data: textures, masks and splats. Cached by default like the rest; the one tier a user may opt out of, so a
    /// client honours the opt-out by skipping this tier and nothing else. Of the splat levels a device keeps the one it
    /// renders (by <see cref="ManifestAsset.Splats"/>).
    /// </summary>
    public const string View3d = "view3d";
}
