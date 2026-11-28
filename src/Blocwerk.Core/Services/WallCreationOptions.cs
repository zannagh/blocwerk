// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services;

/// <summary>
/// Limits on creating walls, bound from the <c>Walls</c> configuration section (env <c>WALLS__MAXOWNEDPERUSER</c>).
/// Applied by <see cref="WallService.CreateWallAsync"/>, so the web create page and the companion app share them.
/// </summary>
public sealed class WallCreationOptions
{
    /// <summary>The configuration section name.</summary>
    public const string Section = "Walls";

    /// <summary>The most walls one user may own at once; 0 or less turns the cap off. Default 20.</summary>
    public int MaxOwnedPerUser { get; set; } = 20;
}
