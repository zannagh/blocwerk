// <copyright file="PanelPhotoScore.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Refresh;

/// <summary>
/// How well an uploaded photo can replace a panel photo, from the homography that maps the CURRENT panel photo
/// (normalized) into the uploaded one (normalized): how much of the panel it shows, how frontal it is compared
/// with the current photo, and whether the panel fills the frame (a wide shot of the whole wall is a poor panel photo).
/// </summary>
public static class PanelPhotoScore
{
    private const int Grid = 12;

    /// <summary>Scores one (photo, panel) pair. <paramref name="sharpness"/> is 0..1 relative to the batch.</summary>
    public static PanelPhotoCandidate Score(Guid photoId, Homography oldToPhoto, double sharpness)
    {
        var coverage = Coverage(oldToPhoto);
        var frontal = Frontal(oldToPhoto);
        var centreScale = AreaScale(oldToPhoto, 0.5, 0.5);
        var fill = double.IsNaN(centreScale) ? 0 : Math.Clamp(Math.Abs(centreScale) / 0.6, 0, 1);
        var score = coverage * Math.Sqrt(frontal) * (0.5 + (0.5 * fill)) * (0.8 + (0.2 * Math.Clamp(sharpness, 0, 1)));
        return new PanelPhotoCandidate(photoId, Math.Round(score, 4), Math.Round(coverage, 3), Math.Round(frontal, 3));
    }

    /// <summary>Share of a grid over the current panel photo that lands inside the uploaded photo.</summary>
    public static double Coverage(Homography oldToPhoto)
    {
        var inside = 0;
        for (var i = 0; i < Grid; i++)
        {
            for (var j = 0; j < Grid; j++)
            {
                var (x, y) = ProjectForward(oldToPhoto, (i + 0.5) / Grid, (j + 0.5) / Grid);
                if (x is >= 0 and <= 1 && y is >= 0 and <= 1)
                {
                    inside++;
                }
            }
        }

        return inside / (double)(Grid * Grid);
    }

    /// <summary>
    /// Smallest over largest local area scale across the panel's corners: 1 for a pure affine (same viewing angle),
    /// lower the more the perspective differs; 0 when the mapping folds.
    /// </summary>
    public static double Frontal(Homography oldToPhoto)
    {
        double[] scales =
        [
            AreaScale(oldToPhoto, 0, 0), AreaScale(oldToPhoto, 1, 0), AreaScale(oldToPhoto, 0, 1), AreaScale(oldToPhoto, 1, 1),
        ];
        if (scales.Any(double.IsNaN) || (scales.Any(s => s <= 0) && scales.Any(s => s >= 0)))
        {
            return 0;
        }

        var abs = scales.Select(Math.Abs).ToArray();
        return abs.Max() <= 1e-12 ? 0 : abs.Min() / abs.Max();
    }

    /// <summary>The Jacobian determinant of the mapping at (x, y), by central differences.</summary>
    public static double AreaScale(Homography h, double x, double y)
    {
        const double d = 1e-3;
        var (x1, y1) = ProjectForward(h, x + d, y);
        var (x0, y0) = ProjectForward(h, x - d, y);
        var (x3, y3) = ProjectForward(h, x, y + d);
        var (x2, y2) = ProjectForward(h, x, y - d);
        var dxdx = (x1 - x0) / (2 * d);
        var dydx = (y1 - y0) / (2 * d);
        var dxdy = (x3 - x2) / (2 * d);
        var dydy = (y3 - y2) / (2 * d);
        return (dxdx * dydy) - (dxdy * dydx);
    }

    private static (double X, double Y) ProjectForward(Homography h, double x, double y)
    {
        var w = (h.M[6] * x) + (h.M[7] * y) + h.M[8];
        if (w <= 1e-9)
        {
            return (double.NaN, double.NaN);
        }

        return (((h.M[0] * x) + (h.M[1] * y) + h.M[2]) / w, ((h.M[3] * x) + (h.M[4] * y) + h.M[5]) / w);
    }
}
