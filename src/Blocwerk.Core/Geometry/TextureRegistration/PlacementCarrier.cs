// <copyright file="PlacementCarrier.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.Registration;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// Carries a hold's placement on an earlier model into a newer one. A registered model is rewritten into its
/// reference model's world frame (<c>quality.registration</c>, <see cref="Registration.WallFrameRegistrationWriter"/>),
/// and every model of a wall is registered to the one active before it, so all of them share one world frame and
/// the facet ids are stable (a facet is claimed by its markers). The previous plane position is lifted into that
/// world frame through the previous facet frame and dropped onto the new facet with the same id. Pure.
/// </summary>
public static class PlacementCarrier
{
    /// <summary>
    /// Farthest the carried point may lie off the new facet's plane, mm. A re-solve moves a plane by millimetres;
    /// more means the facet is not the same surface any more.
    /// </summary>
    public const double MaxPlaneOffsetMm = 60;

    /// <summary>The previous placement in the new facet's plane coordinates, or null when it does not land on that facet.</summary>
    /// <param name="previous">The facet's frame in the model the hold was placed on.</param>
    /// <param name="current">The same facet's frame in the new model.</param>
    /// <param name="extent">The new facet's extent.</param>
    /// <param name="a">Previous plane a, mm.</param>
    /// <param name="b">Previous plane b, mm.</param>
    /// <returns>The new (a, b), mm.</returns>
    public static (double A, double B)? Carry(FacetFrame previous, FacetFrame current, PlaneRectMm extent, double a, double b)
    {
        var world = previous.ToWorld(a, b);
        var d = Vec3.Sub(world, current.Origin);
        var (na, nb, off) = (Vec3.Dot(d, current.U), Vec3.Dot(d, current.V), Vec3.Dot(d, current.Normal));
        var margin = HoldTexturePlacer.ExtentMarginMm;
        var inside = na >= extent.AMin - margin && na <= extent.AMax + margin && nb >= extent.BMin - margin && nb <= extent.BMax + margin;
        return double.IsFinite(na) && double.IsFinite(nb) && Math.Abs(off) <= MaxPlaneOffsetMm && inside ? (na, nb) : null;
    }
}
