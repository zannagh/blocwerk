// <copyright file="CloseUpWarp.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.HybridZoom;

/// <summary>
/// The homography that lays a close-up photo over a panel view. The exact mapping (panel point → facet plane →
/// world → close-up lens) is projective up to the lens distortion, which over a small crop is a fraction of a
/// pixel; one homography fitted to the sampled grid lets the renderer warp the whole crop in one draw, and its
/// largest residual says how far that approximation is off. Pure: no I/O.
/// </summary>
public static class CloseUpWarp
{
    /// <summary>Fits view (u, v) → photo px to the samples.</summary>
    /// <param name="view">The samples' view coordinates (0..1).</param>
    /// <param name="photo">Their photo pixels, same order.</param>
    /// <returns>The homography and its largest residual (px), or null when degenerate.</returns>
    public static (PlaneHomography ViewToPhoto, double ResidualPx)? Fit(
        IReadOnlyList<(double U, double V)> view, IReadOnlyList<(double X, double Y)> photo)
    {
        if (view.Count != photo.Count || view.Count < 4)
        {
            return null;
        }

        var pairs = view.Zip(photo, (v, p) => new PointCorrespondence(v.U, v.V, p.X, p.Y)).ToList();
        var h = PlaneHomography.Fit(pairs);
        if (h is null)
        {
            return null;
        }

        var worst = 0.0;
        foreach (var p in pairs)
        {
            var (x, y) = h.Apply(p.SrcX, p.SrcY);
            if (!double.IsFinite(x) || !double.IsFinite(y))
            {
                return null;
            }

            worst = Math.Max(worst, Math.Sqrt(((x - p.DstX) * (x - p.DstX)) + ((y - p.DstY) * (y - p.DstY))));
        }

        return (h, worst);
    }

    /// <summary>
    /// The 3×3 matrix (row-major) that draws the photo, decoded at <paramref name="decodeScale"/> of its size,
    /// onto an output image of <paramref name="width"/> × <paramref name="height"/> px showing the view:
    /// decoded px → output px, the inverse of output px → view → photo px → decoded px.
    /// </summary>
    /// <param name="viewToPhoto">View (u, v) → photo px.</param>
    /// <param name="width">Output width, px.</param>
    /// <param name="height">Output height, px.</param>
    /// <param name="decodeScale">Decoded size ÷ stored size of the photo (1 = full size).</param>
    /// <returns>The matrix, or null when singular.</returns>
    public static double[]? DecodedToOutput(PlaneHomography viewToPhoto, int width, int height, double decodeScale)
    {
        // output px → view: diag(1/W, 1/H, 1); photo px → decoded px: diag(s, s, 1).
        var toView = PlaneHomography.FromCoefficients([1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1]);
        var toDecoded = PlaneHomography.FromCoefficients([decodeScale, 0, 0, 0, decodeScale, 0, 0, 0, 1]);
        return toView.Then(viewToPhoto).Then(toDecoded).Inverse()?.Coefficients;
    }
}
