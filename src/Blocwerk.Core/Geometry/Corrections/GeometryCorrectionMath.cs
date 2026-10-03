// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Corrections;

/// <summary>Where a tapped pixel's ray met the model: the facet and the world point.</summary>
/// <param name="FacetId">The facet hit first.</param>
/// <param name="World">The point, world mm.</param>
public sealed record TapHit(string FacetId, double[] World);

/// <summary>A measured distance turned into a scale: the model's length between the two hits and the factor.</summary>
/// <param name="Scale">Factor that makes the model's length equal the measured one.</param>
/// <param name="ModelMm">The distance in the model before the correction, mm.</param>
/// <param name="A">The first tap's hit.</param>
/// <param name="B">The second tap's hit.</param>
public sealed record ScaleMeasurement(double Scale, double ModelMm, TapHit A, TapHit B);

/// <summary>
/// The math of the model corrections: two taps on a capture photo plus a measured distance → the scale factor (each
/// tap's viewing ray onto the nearest facet plane it meets inside the facet's extent), and "this surface is vertical" →
/// the rotation that makes that facet plumb (the smallest turn of "up" into the facet's plane).
/// </summary>
public static class GeometryCorrectionMath
{
    /// <summary>The smallest distance two taps may be apart in the model, mm.</summary>
    public const double MinModelMm = 100;

    /// <summary>The largest correction a measured distance may make (factor 1/2 .. 2).</summary>
    public const double MaxScaleFactor = 2;

    /// <summary>The largest tilt a surface may have to be declared vertical, degrees.</summary>
    public const double MaxPlumbTiltDeg = 25;

    /// <summary>How far outside a facet's extent a tap may still land on it, mm.</summary>
    private const double ExtentMarginMm = 150;

    /// <summary>The scale from two taps on a photo (stored-photo pixels) and the millimetres between them.</summary>
    /// <param name="document">The model.</param>
    /// <param name="camera">The photo's solved camera, at the stored photo's resolution.</param>
    /// <param name="a">First tap [x, y] px.</param>
    /// <param name="b">Second tap [x, y] px.</param>
    /// <param name="mm">The measured distance, mm.</param>
    /// <returns>The measurement, or the reason it cannot be used.</returns>
    public static (ScaleMeasurement? Result, string? Refusal) MeasureScale(
        WallGeometryDocument document, SolvedCamera camera, double[] a, double[] b, double mm)
    {
        if (!(mm > 0) || !double.IsFinite(mm))
        {
            return (null, "Enter the distance in millimetres.");
        }

        var hitA = Hit(document, camera, a[0], a[1]);
        var hitB = Hit(document, camera, b[0], b[1]);
        if (hitA is null || hitB is null)
        {
            return (null, "A tapped point is not on any surface of the 3D model. Tap two points on the wall itself.");
        }

        var model = Distance(hitA.World, hitB.World);
        if (model < MinModelMm)
        {
            return (null, "The two points are too close together. Pick two points at least a hand span apart, the farther the better.");
        }

        var scale = mm / model;
        if (scale > MaxScaleFactor || scale < 1 / MaxScaleFactor)
        {
            return (null, string.Create(
                CultureInfo.InvariantCulture,
                $"The model puts these points {model:0} mm apart; {mm:0} mm would change every size by more than half. Check the distance and the points."));
        }

        return (new ScaleMeasurement(scale, model, hitA, hitB), null);
    }

    /// <summary>The nearest facet (inside its extent plus a margin) the pixel's viewing ray meets in front of the camera.</summary>
    /// <param name="document">The model.</param>
    /// <param name="camera">The camera.</param>
    /// <param name="px">Pixel x.</param>
    /// <param name="py">Pixel y.</param>
    /// <returns>The hit, or null.</returns>
    public static TapHit? Hit(WallGeometryDocument document, SolvedCamera camera, double px, double py)
    {
        var centre = camera.Centre;
        TapHit? best = null;
        var bestDistance = double.PositiveInfinity;
        foreach (var facet in document.Segments.SelectMany(s => s.Facets))
        {
            if (FacetFrame.From(facet) is not { } frame || camera.PixelToPlane(frame, px, py) is not { } ab || !Inside(facet.ExtentMm, ab))
            {
                continue;
            }

            var world = frame.ToWorld(ab.A, ab.B);
            var distance = Distance(world, centre);
            if (distance < bestDistance)
            {
                (best, bestDistance) = (new TapHit(facet.Id, world), distance);
            }
        }

        return best;
    }

    /// <summary>The rotation (about the facet's origin) after which facet <paramref name="facetId"/> is plumb, and the new "up".</summary>
    /// <param name="document">The model.</param>
    /// <param name="facetId">The surface declared vertical.</param>
    /// <returns>The rotation and the tilt it removes, or the reason it cannot be done.</returns>
    public static (GeometrySimilarity? Rotation, double TiltDeg, string? Refusal) PlumbRotation(WallGeometryDocument document, string facetId)
    {
        if (document.FindFacet(facetId) is not { } found || FacetFrame.From(found.Facet) is not { } frame)
        {
            return (null, 0, "The model has no such surface.");
        }

        var up = document.World?.Up is { Length: 3 } u && u.All(double.IsFinite) ? GeometrySimilarity.Unit(u) : [0, 0, 1];
        var n = frame.Normal;
        var along = GeometrySimilarity.Dot(up, n);
        double[] inPlane = [up[0] - (along * n[0]), up[1] - (along * n[1]), up[2] - (along * n[2])];
        if (Math.Sqrt(GeometrySimilarity.Dot(inPlane, inPlane)) < 0.2)
        {
            return (null, 0, "That surface is nearly horizontal, so it cannot be the vertical one.");
        }

        var tilt = WallGeometryModelTransformer.TiltDeg(n, up);
        if (Math.Abs(tilt) > MaxPlumbTiltDeg)
        {
            return (null, tilt, string.Create(
                CultureInfo.InvariantCulture,
                $"That surface leans {Math.Abs(tilt):0}° in this model; only a surface within {MaxPlumbTiltDeg:0}° of vertical can be declared vertical."));
        }

        // Turn the facet's own "up" onto the world's z axis: the world ends up z-up with this facet plumb.
        return (GeometrySimilarity.RotationBetween(inPlane, [0, 0, 1], frame.Origin), tilt, null);
    }

    private static bool Inside(PlaneRectMm? extent, (double A, double B) ab) =>
        extent is not { Area: > 0 } e
        || (ab.A >= e.AMin - ExtentMarginMm && ab.A <= e.AMax + ExtentMarginMm && ab.B >= e.BMin - ExtentMarginMm && ab.B <= e.BMax + ExtentMarginMm);

    private static double Distance(double[] p, double[] q) =>
        Math.Sqrt(Math.Pow(p[0] - q[0], 2) + Math.Pow(p[1] - q[1], 2) + Math.Pow(p[2] - q[2], 2));
}
