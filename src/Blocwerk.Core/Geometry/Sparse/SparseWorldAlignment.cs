// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Geometry.Corrections;
using Blocwerk.Core.Geometry.Footprints;
using Blocwerk.Core.Geometry.Registration;

namespace Blocwerk.Core.Geometry.Sparse;

/// <summary>How a sparse cloud was tied to a model's wall world.</summary>
/// <param name="Similarity">Reconstruction frame → wall world (mm).</param>
/// <param name="Used">Photos whose centres the fit used.</param>
/// <param name="RmsMm">Their residual, mm.</param>
public sealed record SparseAlignment(GeometrySimilarity Similarity, int Used, double RmsMm);

/// <summary>
/// Ties a <see cref="SparseCloud"/> to a geometry model's wall world through the camera centres both know by photo stem: a
/// similarity (scale from the centres' spread, rotation by Horn's method), refitted without the photos far off it (never fewer than half of them).
/// The model's cameras follow every rebase and correction of the model, so the fit always lands in the ACTIVE frame. For a
/// feature model built from this very reconstruction it is exact; for a marker model it is as good as COLMAP's poses
/// (≈ 20–30 mm on The Attic). Null when fewer than <see cref="MinPhotos"/> photos match or the fit is poor.
/// </summary>
public static class SparseWorldAlignment
{
    /// <summary>Fewest shared photos.</summary>
    public const int MinPhotos = 4;

    /// <summary>Largest accepted residual, mm.</summary>
    public const double MaxRmsMm = 60;

    private const double OutlierMm = 150;

    /// <summary>Fits the similarity.</summary>
    /// <param name="centres">Photo centres of the reconstruction, by stem.</param>
    /// <param name="cameras">The model's solved cameras.</param>
    /// <returns>The alignment, or null.</returns>
    public static SparseAlignment? Fit(IReadOnlyDictionary<string, double[]> centres, IReadOnlyList<SolvedCamera> cameras)
    {
        var kept = cameras
            .Where(c => centres.ContainsKey(c.Image))
            .GroupBy(c => c.Image, StringComparer.Ordinal)
            .Select(g => (From: centres[g.Key], To: g.First().Centre))
            .ToList();
        var floor = Math.Max(MinPhotos, (kept.Count + 1) / 2);
        while (true)
        {
            if (FitPairs(kept) is not { } fit)
            {
                return null;
            }

            // The photo furthest off goes while it is an outlier (or the fit is poor), never below half of them.
            var residuals = kept.Select(p => Residual(fit, p)).ToList();
            var worst = residuals.IndexOf(residuals.Max());
            var limit = Math.Max(OutlierMm, 3 * residuals.Order().ElementAt(residuals.Count / 2));
            var rms = Rms(fit, kept);
            if ((residuals[worst] <= limit && rms <= MaxRmsMm) || kept.Count <= floor)
            {
                return rms <= MaxRmsMm ? new SparseAlignment(fit, kept.Count, Math.Round(rms, 1)) : null;
            }

            kept.RemoveAt(worst);
        }
    }

    /// <summary>The cloud's points in the wall world, keeping the reliable ones (track and error limits).</summary>
    /// <param name="cloud">The cloud.</param>
    /// <param name="alignment">Its alignment.</param>
    /// <param name="minTrack">Fewest images per point.</param>
    /// <param name="maxErrorPx">Largest reprojection error, px.</param>
    /// <returns>World points, mm.</returns>
    public static List<(float X, float Y, float Z)> WorldPoints(SparseCloud cloud, SparseAlignment alignment, int minTrack = 3, double maxErrorPx = 2)
    {
        var s = alignment.Similarity;
        var result = new List<(float X, float Y, float Z)>(cloud.Count);
        for (var i = 0; i < cloud.Count; i++)
        {
            if (cloud.Track[i] < minTrack || cloud.Error[i] > maxErrorPx)
            {
                continue;
            }

            var p = s.Apply([cloud.Xyz[3 * i], cloud.Xyz[(3 * i) + 1], cloud.Xyz[(3 * i) + 2]]);
            result.Add(((float)p[0], (float)p[1], (float)p[2]));
        }

        return result;
    }

    private static GeometrySimilarity? FitPairs(List<(double[] From, double[] To)> pairs)
    {
        if (pairs.Count < MinPhotos)
        {
            return null;
        }

        var cFrom = Mean(pairs.Select(p => p.From));
        var cTo = Mean(pairs.Select(p => p.To));
        var spreadFrom = Math.Sqrt(pairs.Sum(p => Square(Vec3.Distance(p.From, cFrom))));
        var spreadTo = Math.Sqrt(pairs.Sum(p => Square(Vec3.Distance(p.To, cTo))));
        if (spreadFrom < 1e-9 || spreadTo < 1e-6)
        {
            return null;
        }

        var scale = spreadTo / spreadFrom;
        var rigid = RigidTransform3D.Fit(pairs.Select(p => (Vec3.Scale(p.From, scale), p.To)).ToList());
        if (rigid is null)
        {
            return null;
        }

        var r = rigid.Rotation;
        return new GeometrySimilarity(scale, [r[0, 0], r[0, 1], r[0, 2], r[1, 0], r[1, 1], r[1, 2], r[2, 0], r[2, 1], r[2, 2]], rigid.Translation);
    }

    private static double Residual(GeometrySimilarity s, (double[] From, double[] To) p) => Vec3.Distance(s.Apply(p.From), p.To);

    private static double Rms(GeometrySimilarity s, List<(double[] From, double[] To)> pairs) =>
        pairs.Count == 0 ? double.PositiveInfinity : Math.Sqrt(pairs.Average(p => Square(Residual(s, p))));

    private static double[] Mean(IEnumerable<double[]> points)
    {
        var list = points.ToList();
        return [list.Average(p => p[0]), list.Average(p => p[1]), list.Average(p => p[2])];
    }

    private static double Square(double x) => x * x;
}
