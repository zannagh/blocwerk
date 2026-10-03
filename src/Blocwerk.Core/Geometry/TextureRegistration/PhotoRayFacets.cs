// <copyright file="PhotoRayFacets.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>One facet of the model: its 3D frame and its extent on its plane.</summary>
public sealed record ModelFacet(FacetFrame Frame, PlaneRectMm Extent);

/// <summary>
/// Whether a photo point's ray misses every facet of the model: the camera pose comes from the photo's best accepted
/// registration (as <see cref="FacetViewPrediction"/> recovers it), and the ray is intersected with each facet's plane.
/// Answers "misses" only when it can tell: without a pose, or with a facet it cannot map, the answer is no.
/// </summary>
public static class PhotoRayFacets
{
    /// <summary>A ray this close (mm) to a facet's extent still counts as hitting it (pose and model errors).</summary>
    public const double HitMarginMm = 150;

    /// <summary>Whether the ray through the normalised photo point (x, y) misses every facet.</summary>
    /// <param name="registrations">The photo's registrations.</param>
    /// <param name="facets">Every facet of the model, by id.</param>
    /// <param name="width">Photo width, px.</param>
    /// <param name="height">Photo height, px.</param>
    /// <param name="focalPx">The focal length when the registration cannot tell it (EXIF), or null.</param>
    /// <param name="x">Normalised x.</param>
    /// <param name="y">Normalised y.</param>
    /// <returns>True only when every facet is missed.</returns>
    public static bool MissesEveryFacet(
        IReadOnlyList<FacetRegistration> registrations, IReadOnlyDictionary<string, ModelFacet> facets, int width, int height, double? focalPx, double x, double y)
    {
        if (facets.Count == 0 || Pose(registrations, facets, width, height, focalPx) is not { } pose)
        {
            return false;
        }

        foreach (var facet in facets.Values)
        {
            if (pose.PlaneToPhoto(facet.Frame).Inverse() is not { } photoToPlane)
            {
                return false;
            }

            var (a, b) = photoToPlane.Apply(x * width, y * height);
            if (!double.IsFinite(a) || !double.IsFinite(b) || pose.Depth(facet.Frame, a, b) <= 0)
            {
                continue;
            }

            if (a >= facet.Extent.AMin - HitMarginMm && a <= facet.Extent.AMax + HitMarginMm
                && b >= facet.Extent.BMin - HitMarginMm && b <= facet.Extent.BMax + HitMarginMm)
            {
                return false;
            }
        }

        return true;
    }

    private static CameraPose? Pose(
        IReadOnlyList<FacetRegistration> registrations, IReadOnlyDictionary<string, ModelFacet> facets, int width, int height, double? focalPx)
    {
        var anchor = registrations.Where(r => r.Accepted && r.PhotoToPlane is not null && facets.ContainsKey(r.FacetId)).MaxBy(r => r.Inliers);
        if (anchor is null || width <= 0 || height <= 0)
        {
            return null;
        }

        var pixelToNormalised = PlaneHomography.FromCoefficients([1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1]);
        if (pixelToNormalised.Then(anchor.PhotoToPlane!).Inverse() is not { } planeToPhoto)
        {
            return null;
        }

        var g = planeToPhoto.Coefficients;
        var (cx, cy) = (width / 2.0, height / 2.0);
        var f = FacetViewPrediction.SelfCalibratedFocal(g, cx, cy, Math.Max(width, height)) ?? focalPx;
        var (ca, cb) = anchor.Map(0.5, 0.5);
        return f is > 0 && double.IsFinite(ca) && double.IsFinite(cb)
            ? CameraPose.FromHomography(g, f.Value, cx, cy, ca, cb, facets[anchor.FacetId].Frame)
            : null;
    }
}
