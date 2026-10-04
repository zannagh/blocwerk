// <copyright file="HoldMoveCalculator.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.TextureRegistration;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.HoldMoves;

/// <summary>
/// Measures how far an old hold and its successor are apart on the wall. Prefers 3D (both holds placed on the same
/// facet of the model: the in-plane distance, plus the difference in how far they stand out when both were measured);
/// otherwise the photo warp's prediction error in pixels, converted with the panel's scale. Pure.
/// </summary>
public static class HoldMoveCalculator
{
    /// <summary>The 3D distance, mm, or null when the pair has no comparable placements.</summary>
    /// <param name="old">The old hold.</param>
    /// <param name="twin">Its successor.</param>
    /// <returns>The distance, or null.</returns>
    public static double? Distance3D(Hold old, Hold twin) =>
        Vector3D(old, twin) is { } v ? Math.Sqrt((v.Da * v.Da) + (v.Db * v.Db) + (v.Dh * v.Dh)) : null;

    /// <summary>The raw displacement old to new on a shared facet (across, up, out of the wall), mm, or null.</summary>
    /// <param name="old">The old hold.</param>
    /// <param name="twin">Its successor.</param>
    /// <returns>The vector, or null when the pair has no comparable placements.</returns>
    public static (double Da, double Db, double Dh)? Vector3D(Hold old, Hold twin)
    {
        if (!HasPlacement(old) || !HasPlacement(twin) || old.FacetId != twin.FacetId)
        {
            return null;
        }

        var dh = Height(old) is { } a && Height(twin) is { } b ? b - a : 0;
        return (twin.PlaneAMm!.Value - old.PlaneAMm!.Value, twin.PlaneBMm!.Value - old.PlaneBMm!.Value, dh);
    }

    /// <summary>The photo-warp estimate, mm, or null when the old hold has no predicted spot or the panel no scale.</summary>
    /// <param name="twin">The successor on the new photo.</param>
    /// <param name="warped">Where the matcher predicts the old hold on the new photo.</param>
    /// <param name="size">The new photo's size, px.</param>
    /// <param name="mmPerPx">The panel's scale.</param>
    /// <returns>The distance, or null.</returns>
    public static double? Distance2D(Hold twin, HoldPositionNorm? warped, (int Width, int Height)? size, double? mmPerPx)
    {
        if (warped is null || size is not { } s || mmPerPx is not { } scale)
        {
            return null;
        }

        var dx = (twin.X - warped.X) * s.Width;
        var dy = (twin.Y - warped.Y) * s.Height;
        return Math.Sqrt((dx * dx) + (dy * dy)) * scale;
    }

    /// <summary>How far an elongated hold turned (0..90 degrees), or null when either fingerprint cannot tell.</summary>
    /// <param name="old">The old hold's fingerprint.</param>
    /// <param name="twin">The successor's fingerprint.</param>
    /// <returns>The angle, or null.</returns>
    public static double? Rotation(HoldFingerprint? old, HoldFingerprint? twin)
    {
        if (old is not { Aspect: >= 1.3 } || twin is not { Aspect: >= 1.3 })
        {
            return null;
        }

        var d = Math.Abs(old.OrientationDeg - twin.OrientationDeg) % 180;
        return Math.Min(d, 180 - d);
    }

    /// <summary>The measurement for a pair: 3D when both are placed, else the 2D estimate, else null.</summary>
    /// <param name="old">The old hold.</param>
    /// <param name="twin">Its successor.</param>
    /// <param name="warped">The matcher's predicted spot of the old hold on the new photo.</param>
    /// <param name="size">The new photo's size.</param>
    /// <param name="mmPerPx">The panel's scale.</param>
    /// <returns>The measurement, or null when nothing can be said.</returns>
    public static HoldMoveMeasure? Measure(
        Hold old, Hold twin, HoldPositionNorm? warped, (int Width, int Height)? size, double? mmPerPx)
    {
        var rotation = Rotation(HoldFingerprint.FromJson(old.FingerprintJson), HoldFingerprint.FromJson(twin.FingerprintJson));
        if (Distance3D(old, twin) is { } d3)
        {
            return new HoldMoveMeasure(d3, HoldMoveSource.ThreeD, rotation);
        }

        return Distance2D(twin, warped, size, mmPerPx) is { } d2 ? new HoldMoveMeasure(d2, HoldMoveSource.TwoD, rotation) : null;
    }

    private static bool HasPlacement(Hold h) =>
        h is { FacetId: not null, PlaneAMm: not null, PlaneBMm: not null } && !HoldTexturePlacer.IsRejected(h);

    private static double? Height(Hold h) =>
        HoldProtrusion.For(h) is { Source: not HoldProtrusionSource.Estimate } p ? p.ApexMm : null;
}
