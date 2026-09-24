// <copyright file="FacetViewPrediction.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.TextureRegistration;

/// <summary>
/// Predicts where a photo shows a facet it could not be matched to directly, from a facet of the same photo
/// that did register. A registration is a plane → photo homography <c>G ∝ K·[R·u | R·v | R·o + t]</c>; with the
/// focal length (self-calibrated from G, principal point at the centre) it yields the camera pose, and the
/// model's 3D facet frames then give the other facet's homography. The prediction is only a seed for guided
/// matching: the matches found around it are judged by <see cref="PhotoTextureRegistration"/> like any other.
/// Pure: no I/O.
/// </summary>
public static class FacetViewPrediction
{
    /// <summary>Plausible focal lengths, as a multiple of the photo's longer side (ultra-wide to 3× tele).</summary>
    private const double MinFocalShare = 0.3;

    private const double MaxFocalShare = 3.5;

    /// <summary>Predicts the photo px → target texture px homography.</summary>
    /// <param name="anchor">An accepted registration of the same photo.</param>
    /// <param name="anchorFrame">The anchor facet's 3D frame.</param>
    /// <param name="target">The target facet's 3D frame.</param>
    /// <param name="texture">The target texture's grid on its plane.</param>
    /// <param name="width">Photo width, px.</param>
    /// <param name="height">Photo height, px.</param>
    /// <param name="fallbackFocalPx">The focal length when the anchor alone cannot tell it (e.g. from EXIF), or null.</param>
    /// <returns>The row-major 3×3 homography, or null when the target is not predicted in view.</returns>
    public static double[]? PhotoToTexture(
        FacetRegistration anchor, FacetFrame anchorFrame, FacetFrame target, TexturePlaneFrame texture, int width, int height, double? fallbackFocalPx)
    {
        if (!anchor.Accepted || anchor.PhotoToPlane is null || width <= 0 || height <= 0)
        {
            return null;
        }

        var pixelToNormalised = PlaneHomography.FromCoefficients([1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1]);
        if (pixelToNormalised.Then(anchor.PhotoToPlane).Inverse() is not { } planeToPhoto)
        {
            return null;
        }

        var g = planeToPhoto.Coefficients;
        var (cx, cy) = (width / 2.0, height / 2.0);
        var f = SelfCalibratedFocal(g, cx, cy, Math.Max(width, height)) ?? fallbackFocalPx;
        var (ca, cb) = anchor.Map(0.5, 0.5);
        if (f is not > 0 || !double.IsFinite(ca) || !double.IsFinite(cb))
        {
            return null;
        }

        var pose = CameraPose.FromHomography(g, f.Value, cx, cy, ca, cb, anchorFrame);
        if (pose is null || !pose.Faces(target))
        {
            return null;
        }

        var targetToPhoto = pose.PlaneToPhoto(target);
        if (targetToPhoto.Inverse() is not { } photoToPlane || !InView(pose, target, targetToPhoto, texture, width, height))
        {
            return null;
        }

        return photoToPlane.Then(texture.PixelToPlane().Inverse()!).Coefficients;
    }

    /// <summary>
    /// The focal length that makes the plane's two axes orthogonal and equally long in camera space (least
    /// squares over both constraints), or null when the view is too frontal to tell or the result implausible.
    /// </summary>
    /// <param name="g">Plane → photo px, row-major.</param>
    /// <param name="cx">Principal point x.</param>
    /// <param name="cy">Principal point y.</param>
    /// <param name="side">The photo's longer side, px.</param>
    /// <returns>The focal length in px.</returns>
    internal static double? SelfCalibratedFocal(double[] g, double cx, double cy, double side)
    {
        // centre the image coordinates: rows 0 and 1 minus c × row 2
        double a1 = g[0] - (cx * g[6]), b1 = g[3] - (cy * g[6]), c1 = g[6];
        double a2 = g[1] - (cx * g[7]), b2 = g[4] - (cy * g[7]), c2 = g[7];
        double p1 = (a1 * a2) + (b1 * b2), q1 = c1 * c2;
        double p2 = (a1 * a1) + (b1 * b1) - (a2 * a2) - (b2 * b2), q2 = (c1 * c1) - (c2 * c2);
        var denominator = (p1 * p1) + (p2 * p2);
        if (denominator <= 0)
        {
            return null;
        }

        var w = -((p1 * q1) + (p2 * q2)) / denominator;
        if (w <= 0)
        {
            return null;
        }

        var f = 1 / Math.Sqrt(w);
        return f >= MinFocalShare * side && f <= MaxFocalShare * side ? f : null;
    }

    /// <summary>Whether enough of the texture's grid projects in front of the camera and inside the photo.</summary>
    private static bool InView(CameraPose pose, FacetFrame facet, PlaneHomography planeToPhoto, TexturePlaneFrame texture, int width, int height)
    {
        const int n = 12;
        var inside = 0;
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                var a = texture.AMin + ((i + 0.5) * (texture.AMax - texture.AMin) / n);
                var b = texture.BMin + ((j + 0.5) * (texture.BMax - texture.BMin) / n);
                var (x, y) = planeToPhoto.Apply(a, b);
                var front = pose.Depth(facet, a, b) > 0;
                inside += front && x >= 0 && x < width && y >= 0 && y < height ? 1 : 0;
            }
        }

        return inside >= n * n * PhotoTextureRegistration.MinOverlapShare;
    }
}
