// <copyright file="TextureSpotLocator.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.TextureRegistration;

namespace Blocwerk.Core.Services;

/// <summary>Where a photo point lands on the 3D model's textures, when the registration supports it there.</summary>
/// <param name="Registration">The facet registration that maps the point.</param>
/// <param name="A">Plane a, mm.</param>
/// <param name="B">Plane b, mm.</param>
public sealed record TextureSpot(FacetRegistration Registration, double A, double B)
{
    public string FacetId => Registration.FacetId;
}

/// <summary>
/// Maps a normalised photo point onto a facet of the model, but only where the registration is trustworthy: an accepted
/// registration, the point inside the facet's extent, and inside the hull of the registration's inliers (interpolated,
/// never extrapolated). Anywhere else the texture may show unmatched or uncovered content, so there is no spot. Pure.
/// </summary>
public static class TextureSpotLocator
{
    /// <summary>The best supported spot of a photo point (closest to an inlier), or null.</summary>
    /// <param name="registrations">The photo's facet registrations (rejected ones are ignored).</param>
    /// <param name="x">Normalised x.</param>
    /// <param name="y">Normalised y.</param>
    /// <returns>The spot, or null.</returns>
    public static TextureSpot? Locate(IReadOnlyList<FacetRegistration> registrations, double x, double y)
    {
        TextureSpot? best = null;
        var bestMm = double.MaxValue;
        foreach (var r in registrations.Where(r => r.Accepted))
        {
            var (a, b) = r.Map(x, y);
            if (!double.IsFinite(a) || !double.IsFinite(b) || !Inside(r.Extent, a, b))
            {
                continue;
            }

            if (InlierSupport.Of(r) is not { } support || support.BeyondHullMm(a, b) > 0)
            {
                continue;
            }

            var nearest = support.NearestInlierMm(a, b);
            if (nearest < bestMm)
            {
                bestMm = nearest;
                best = new TextureSpot(r, a, b);
            }
        }

        return best;
    }

    /// <summary>Millimetres on the plane per photo pixel at a point (finite differences of the mapping), or null.</summary>
    /// <param name="spot">The spot the point maps to.</param>
    /// <param name="x">Normalised x of the point.</param>
    /// <param name="y">Normalised y of the point.</param>
    /// <param name="width">Photo width, px.</param>
    /// <param name="height">Photo height, px.</param>
    /// <returns>The scale, or null when the mapping degenerates there.</returns>
    public static double? MmPerPhotoPx(TextureSpot spot, double x, double y, int width, int height)
    {
        const double StepPx = 20;
        if (width <= 0 || height <= 0)
        {
            return null;
        }

        var (ax, bx) = spot.Registration.Map(x + (StepPx / width), y);
        var (ay, by) = spot.Registration.Map(x, y + (StepPx / height));
        if (!double.IsFinite(ax) || !double.IsFinite(bx) || !double.IsFinite(ay) || !double.IsFinite(by))
        {
            return null;
        }

        var dx = Math.Sqrt(Sq(ax - spot.A) + Sq(bx - spot.B));
        var dy = Math.Sqrt(Sq(ay - spot.A) + Sq(by - spot.B));
        var mm = (dx + dy) / (2 * StepPx);
        return mm > 0 && double.IsFinite(mm) ? mm : null;
    }

    private static bool Inside(PlaneRectMm r, double a, double b) =>
        a >= r.AMin - HoldTexturePlacer.ExtentMarginMm && a <= r.AMax + HoldTexturePlacer.ExtentMarginMm
        && b >= r.BMin - HoldTexturePlacer.ExtentMarginMm && b <= r.BMax + HoldTexturePlacer.ExtentMarginMm;

    private static double Sq(double v) => v * v;
}
