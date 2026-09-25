// <copyright file="VolumeCoverageRater.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture.Coverage;

/// <summary>
/// Rates each volume's surface: samples its height field, sorts every sample onto a face by its normal in the
/// facet's frame (front, underside, top side, left side, right side) and rates it like a facet cell. The steep sides,
/// the undersides above all, are where captures usually fall short.
/// </summary>
public static class VolumeCoverageRater
{
    /// <summary>Sample spacing over a volume, mm.</summary>
    public const double SampleMm = 30;

    /// <summary>A face is weak when at least this share of its samples is.</summary>
    public const double WeakShare = 0.4;

    /// <summary>Samples lower than this are the wall around the volume, mm.</summary>
    private const double MinHeightMm = 15;

    /// <summary>A normal this close to the facet normal (its facet-frame h) is the front.</summary>
    private const double FrontNh = 0.8;

    /// <summary>Rates the volumes of the scene.</summary>
    /// <param name="scene">The wall.</param>
    /// <param name="cameras">The posed cameras.</param>
    /// <returns>One entry per volume.</returns>
    public static IReadOnlyList<VolumeCoverage> Rate(CoverageScene scene, IReadOnlyList<CoverageCamera> cameras) =>
        scene.Volumes.Select(v => Rate(v, scene.Facet(v.FacetId)!, scene, cameras)).ToList();

    /// <summary>The face a facet-frame normal (na, nb, nh) belongs to.</summary>
    /// <param name="n">The normal.</param>
    /// <returns>The face.</returns>
    public static VolumeFace FaceOf(double[] n)
    {
        if (n[2] >= FrontNh)
        {
            return VolumeFace.Front;
        }

        if (Math.Abs(n[1]) >= Math.Abs(n[0]))
        {
            return n[1] < 0 ? VolumeFace.Underside : VolumeFace.TopSide;
        }

        return n[0] < 0 ? VolumeFace.LeftSide : VolumeFace.RightSide;
    }

    /// <summary>The face's rating: good, or its most common weakness when at least <see cref="WeakShare"/> is weak.</summary>
    /// <param name="counts">The face's sample counts.</param>
    /// <returns>The rating.</returns>
    public static CoverageCellStatus FaceStatus(CoverageCounts counts)
    {
        if (counts.Total == 0 || counts.Weak < counts.Total * WeakShare)
        {
            return CoverageCellStatus.Good;
        }

        (CoverageCellStatus S, int N)[] weak =
        [
            (CoverageCellStatus.Never, counts.Never), (CoverageCellStatus.Grazing, counts.Grazing),
            (CoverageCellStatus.FewDirections, counts.FewDirections), (CoverageCellStatus.LowResolution, counts.LowResolution),
        ];
        return weak.OrderByDescending(w => w.N).First().S;
    }

    private static VolumeCoverage Rate(CoverageVolume volume, CoverageFacet facet, CoverageScene scene, IReadOnlyList<CoverageCamera> cameras)
    {
        var byFace = new Dictionary<VolumeFace, List<CoverageCellStatus>>();
        var g = volume.Surface.Grid;
        double aEnd = g.ALo + (g.Cols * g.CellMm), bEnd = g.BLo + (g.Rows * g.CellMm);
        for (var b = g.BLo + (SampleMm / 2); b < bEnd; b += SampleMm)
        {
            for (var a = g.ALo + (SampleMm / 2); a < aEnd; a += SampleMm)
            {
                var h = volume.Surface.HeightAt(a, b);
                if (h < MinHeightMm)
                {
                    continue;
                }

                var n = volume.Surface.NormalAt(a, b);
                var world = facet.Frame.ToWorld(a, b, h);
                var views = PointViews.Evaluate(world, WorldNormal(facet, n), facet.Id, cameras, scene);
                var face = FaceOf(n);
                if (!byFace.TryGetValue(face, out var list))
                {
                    list = [];
                    byFace[face] = list;
                }

                list.Add(views.Status);
            }
        }

        var faces = byFace.OrderBy(f => f.Key).Select(f => Face(f.Key, CoverageCellCodes.Count(f.Value))).ToList();
        return new VolumeCoverage(volume.Index, volume.FacetId, volume.Footprint, faces);
    }

    private static VolumeFaceCoverage Face(VolumeFace face, CoverageCounts counts) => new(face, counts, FaceStatus(counts));

    private static double[] WorldNormal(CoverageFacet facet, double[] n)
    {
        var f = facet.Frame;
        return
        [
            (n[0] * f.U[0]) + (n[1] * f.V[0]) + (n[2] * f.Normal[0]),
            (n[0] * f.U[1]) + (n[1] * f.V[1]) + (n[2] * f.Normal[1]),
            (n[0] * f.U[2]) + (n[1] * f.V[2]) + (n[2] * f.Normal[2]),
        ];
    }
}
