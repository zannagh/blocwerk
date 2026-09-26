// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.Capture.Corrections;

/// <summary>
/// Where a model's sizes and angles come from, in plain words for the result card ("Sizes: estimated (±10 %)",
/// "Angles: from the floor"), from its <c>world</c> block (<see cref="WallGeometryWorld.ScaleSource"/>,
/// <see cref="WallGeometryWorld.GravitySource"/>, <see cref="WallGeometryWorld.ScaleKnown"/>,
/// <see cref="WallGeometryWorld.GravityKnown"/>).
/// </summary>
public static class GeometrySourceText
{
    /// <summary>The sizes' source.</summary>
    /// <param name="world">The model's world block (null: an old marker model).</param>
    /// <param name="carriedFrom">When the model the scale was carried over from was made, if known.</param>
    /// <returns>E.g. "estimated (±10 %)".</returns>
    public static string Scale(WallGeometryWorld? world, DateTimeOffset? carriedFrom)
    {
        if (world?.IsFeatureFrame != true)
        {
            return "from the printed markers";
        }

        if (world.ScaleIsEstimate)
        {
            return "estimated (±10 %)";
        }

        return world.ScaleSource?.ToLowerInvariant() switch
        {
            "measured" => "measured",
            "anchors" or "anchor-fit" => CarriedOver(carriedFrom),
            _ => "measured",
        };
    }

    /// <summary>The angles' source.</summary>
    /// <param name="world">The model's world block (null: an old marker model).</param>
    /// <param name="carriedFrom">When the model "up" was carried over from was made, if known.</param>
    /// <returns>E.g. "from the floor".</returns>
    public static string Gravity(WallGeometryWorld? world, DateTimeOffset? carriedFrom)
    {
        if (world?.GravityKnown == false)
        {
            return "not measured";
        }

        if (world?.IsFeatureFrame != true)
        {
            return world?.GravitySource == "declared" ? "from the declared vertical surface" : "from the markers' surfaces";
        }

        return world.GravitySource?.ToLowerInvariant() switch
        {
            "floor" => "from the floor",
            "declared" => "from the declared vertical surface",
            "device" => "from the phone's motion sensor",
            "anchors" or "anchor-fit" => CarriedOver(carriedFrom),
            _ => "not measured",
        };
    }

    /// <summary>True when the angles are not measured, so "Which surface is vertical?" is offered.</summary>
    /// <param name="world">The model's world block.</param>
    /// <returns>Whether "up" is unknown.</returns>
    public static bool GravityUnknown(WallGeometryWorld? world) =>
        world?.GravityKnown == false || (world?.IsFeatureFrame == true && world.GravitySource is null or "cameras");

    private static string CarriedOver(DateTimeOffset? from) => from is { } date
        ? string.Create(CultureInfo.InvariantCulture, $"carried over from the model of {date.UtcDateTime:d MMM yyyy}")
        : "carried over from the previous model";
}
