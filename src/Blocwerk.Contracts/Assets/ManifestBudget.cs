// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Contracts.Assets;

/// <summary>
/// The background fill budget (design §6.8, decided): the <c>core</c> tier of at most <see cref="Walls"/> recently
/// opened walls, at most <see cref="WallCoreBytes"/> per wall, filled in tier order. The device-wide budgets are the
/// client's own (they depend on the browser and its quota).
/// </summary>
/// <param name="WallCoreBytes">The per-wall cap of the background fill.</param>
/// <param name="Walls">How many walls the background fill keeps.</param>
public sealed record ManifestBudget(long WallCoreBytes, int Walls)
{
    /// <summary>60 MB per wall.</summary>
    public const long DefaultWallCoreBytes = 60L * 1024 * 1024;

    /// <summary>The 5 most recently opened walls.</summary>
    public const int DefaultWalls = 5;

    /// <summary>Gets the decided budget.</summary>
    public static ManifestBudget Default { get; } = new(DefaultWallCoreBytes, DefaultWalls);
}
