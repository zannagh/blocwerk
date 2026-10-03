// <copyright file="SurfaceYaw.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// The one way a surface's signed yaw is put into words: "30° left", "straight" or "90° right" —
/// never "-90°". The sign convention is the plan's, the solve request's and the solver's
/// (<c>frame.yaw_rel</c>: counter-clockwise seen from above, relative to the root surface): positive =
/// turned left (you turn left to face it): the surface faces toward the viewer's right, i.e. it is on the
/// viewer's left (The Attic's left side triangle measured +89°); negative = turned right, on the viewer's
/// right. <see cref="Entities.WallSegment.Yaw"/> and <see cref="Entities.WallSegment.MeasuredYaw"/> use the
/// same sense, so no conversion happens at any boundary.
/// </summary>
public static class SurfaceYaw
{
    /// <summary>Yaws within this many degrees of the root's heading read as straight.</summary>
    public const double StraightToleranceDeg = 0.5;

    /// <summary>Which way <paramref name="yawDeg"/> turns (|yaw| below the tolerance is straight).</summary>
    public static SurfaceTurn TurnOf(double yawDeg) =>
        Math.Abs(yawDeg) < StraightToleranceDeg ? SurfaceTurn.Straight
        : yawDeg > 0 ? SurfaceTurn.Left
        : SurfaceTurn.Right;

    /// <summary>"30° left", "straight" or "90° right" (one decimal at most, invariant culture).</summary>
    public static string Describe(double yawDeg)
    {
        if (!double.IsFinite(yawDeg))
        {
            return "unknown turn";
        }

        return TurnOf(yawDeg) switch
        {
            SurfaceTurn.Straight => "straight",
            SurfaceTurn.Left => $"{Degrees(yawDeg)}° left",
            _ => $"{Degrees(-yawDeg)}° right",
        };
    }

    /// <summary>The signed yaw for a turn and a (positive) size in degrees; straight is 0.</summary>
    public static double Signed(SurfaceTurn turn, double magnitudeDeg) => turn switch
    {
        SurfaceTurn.Straight => 0,
        SurfaceTurn.Left => Math.Abs(magnitudeDeg),
        _ => -Math.Abs(magnitudeDeg),
    };

    private static string Degrees(double value) => value.ToString("0.#", CultureInfo.InvariantCulture);
}
