// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Assets;

/// <summary>The tiers of <see cref="ManifestAsset.Tier"/>, in fill order (design §6.8).</summary>
public static class ManifestTiers
{
    /// <summary>Filled in the background, within the budget.</summary>
    public const string Core = "core";

    /// <summary>Cached when used.</summary>
    public const string Lazy = "lazy";

    /// <summary>Cached when the 3D view opens.</summary>
    public const string View3d = "view3d";

    /// <summary>Cached only when the user opts in ("Save 3D offline").</summary>
    public const string Optin = "optin";
}
