// <copyright file="FlatSidedBase.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// The base polygon of a flat-sided volume as half-planes: each edge keeps the direction of the simplified detected
/// outline and only moves outwards or inwards (its offset). The detected outline is where the volume is at least
/// ~35 mm high, so the real base (height 0) lies further out: each side's slope is fitted to the height field between
/// the top and the outline, and its edge moved to where that slope meets the wall.
/// </summary>
internal sealed class FlatSidedBase
{
    private const double MaxGrowMm = 150;
    private const double MaxShrinkMm = 20;
    private const double BandLo = 0.2;
    private const double BandHi = 0.85;
    private const int MinCells = 6;

    private readonly (double A, double B)[] normals;
    private readonly double[] start;
    private readonly double[] offsets;

    /// <summary>Initializes a new instance of the <see cref="FlatSidedBase"/> class from a convex counter-clockwise ring.</summary>
    /// <param name="ring">The simplified detected outline.</param>
    public FlatSidedBase(IReadOnlyList<(double A, double B)> ring)
    {
        normals = new (double A, double B)[ring.Count];
        start = new double[ring.Count];
        for (var i = 0; i < ring.Count; i++)
        {
            normals[i] = VolumeHull.Outward(ring[i], ring[(i + 1) % ring.Count]);
            start[i] = (normals[i].A * ring[i].A) + (normals[i].B * ring[i].B);
        }

        offsets = (double[])start.Clone();
    }

    /// <summary>The current polygon (counter-clockwise) and, per vertex i, which half-plane its edge i → i + 1 lies on.</summary>
    /// <returns>The ring and the edge's half-plane index.</returns>
    public (List<(double A, double B)> Ring, int[] Lines) Polygon()
    {
        var pad = offsets.Select(Math.Abs).Max() + 10_000;
        List<(double A, double B)> ring = [(-pad, -pad), (pad, -pad), (pad, pad), (-pad, pad)];
        for (var i = 0; i < normals.Length && ring.Count > 0; i++)
        {
            ring = VolumeRings.ClipHalfPlane(ring, normals[i], offsets[i]);
        }

        ring = Dedupe(ring);
        var lines = new int[ring.Count];
        for (var k = 0; k < ring.Count; k++)
        {
            var n = VolumeHull.Outward(ring[k], ring[(k + 1) % ring.Count]);
            lines[k] = Enumerable.Range(0, normals.Length).MaxBy(i => (normals[i].A * n.A) + (normals[i].B * n.B));
        }

        return (ring, lines);
    }

    /// <summary>Moves every edge to where its side's fitted slope meets the wall (within limits).</summary>
    /// <param name="ring">The current polygon.</param>
    /// <param name="lines">Its edges' half-plane indices.</param>
    /// <param name="top">The top vertices.</param>
    /// <param name="cells">The height field's cells (a, b, height).</param>
    public void Refit(
        IReadOnlyList<(double A, double B)> ring, int[] lines, IReadOnlyList<(double A, double B, double H)> top, IReadOnlyList<(double A, double B, double H)> cells)
    {
        var faces = VolumeHull.Build(ring, top);
        var planes = faces.Select(f => PlaneOf(f.Vertices)).ToArray();
        var height = top.Max(t => t.H);
        var sums = new (double Num, double Den, int Count)[faces.Count];
        foreach (var c in cells.Where(c => c.H >= BandLo * height && c.H <= BandHi * height))
        {
            var k = Enumerable.Range(0, planes.Length).MinBy(f => (planes[f].Alpha * c.A) + (planes[f].Beta * c.B) + planes[f].Gamma);
            var face = faces[k];
            if (face.Edge < 0)
            {
                continue;
            }

            var t = top[face.TopIndex];
            var n = normals[lines[face.Edge]];
            var d = ((c.A - t.A) * n.A) + ((c.B - t.B) * n.B);
            sums[k] = (sums[k].Num + ((t.H - c.H) * d), sums[k].Den + (d * d), sums[k].Count + 1);
        }

        for (var k = 0; k < faces.Count; k++)
        {
            if (faces[k].Edge < 0 || sums[k].Count < MinCells || sums[k].Den <= 0)
            {
                continue;
            }

            var slope = sums[k].Num / sums[k].Den;
            if (slope <= 0.05)
            {
                continue;
            }

            var t = top[faces[k].TopIndex];
            var i = lines[faces[k].Edge];
            var reach = (normals[i].A * t.A) + (normals[i].B * t.B) + (t.H / slope);
            offsets[i] = Math.Clamp(reach, start[i] - MaxShrinkMm, start[i] + MaxGrowMm);
        }
    }

    /// <summary>The plane h = α·a + β·b + γ through a face's first three corners.</summary>
    private static (double Alpha, double Beta, double Gamma) PlaneOf((double A, double B, double H)[] f)
    {
        var (p, q, r) = (f[0], f[1], f[2]);
        double ua = q.A - p.A, ub = q.B - p.B, uh = q.H - p.H;
        double va = r.A - p.A, vb = r.B - p.B, vh = r.H - p.H;
        double na = (ub * vh) - (uh * vb), nb = (uh * va) - (ua * vh), nh = (ua * vb) - (ub * va);
        if (Math.Abs(nh) < 1e-9)
        {
            return (0, 0, double.PositiveInfinity);
        }

        var alpha = -na / nh;
        var beta = -nb / nh;
        return (alpha, beta, p.H - (alpha * p.A) - (beta * p.B));
    }

    private static List<(double A, double B)> Dedupe(List<(double A, double B)> ring)
    {
        var result = new List<(double A, double B)>();
        foreach (var p in ring)
        {
            if (result.Count == 0 || Math.Abs(p.A - result[^1].A) + Math.Abs(p.B - result[^1].B) > 1)
            {
                result.Add(p);
            }
        }

        while (result.Count > 1 && Math.Abs(result[0].A - result[^1].A) + Math.Abs(result[0].B - result[^1].B) <= 1)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result;
    }
}
