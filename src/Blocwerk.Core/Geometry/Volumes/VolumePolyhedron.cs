// <copyright file="VolumePolyhedron.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Geometry.Volumes;

/// <summary>
/// A flat-sided volume: planar faces over its facet in facet coordinates (a, b, height; mm), the convex hull of its
/// base polygon on the wall (height 0) and one apex (a pyramid) or two ridge ends (a roof). Only the faces pointing
/// away from the wall are kept (the base face is the wall). Because the solid is convex and every kept face looks
/// away from the wall, its surface height is the lowest of the face planes, and 0 outside the base.
/// </summary>
public sealed class VolumePolyhedron
{
    /// <summary>Faces whose normals differ less than this (cosine) count as one flat side.</summary>
    private const double SameSideCos = 0.9994;

    private readonly (double A, double B, double H)[][] faces;
    private readonly (double Na, double Nb, double Nh, double D)[] planes;

    /// <summary>Initializes a new instance of the <see cref="VolumePolyhedron"/> class.</summary>
    /// <param name="faces">The faces, each a planar polygon (a, b, height).</param>
    public VolumePolyhedron(IEnumerable<(double A, double B, double H)[]> faces)
    {
        this.faces = faces.Where(f => f.Length >= 3).ToArray();
        planes = this.faces.Select(Plane).Where(p => p.Nh > 1e-6).ToArray();
        Top = this.faces.SelectMany(f => f).Where(v => v.H > 0.5).Distinct().ToList();
        Base = PlanePolygon.ConvexHull(this.faces.SelectMany(f => f).Where(v => v.H <= 0.5).Select(v => (v.A, v.B)));
    }

    /// <summary>The faces (a, b, height), each counter-clockwise seen from outside.</summary>
    public IReadOnlyList<(double A, double B, double H)[]> Faces => faces;

    /// <summary>The base polygon on the wall, counter-clockwise.</summary>
    public IReadOnlyList<(double A, double B)> Base { get; }

    /// <summary>The top vertices: one apex or two ridge ends.</summary>
    public IReadOnlyList<(double A, double B, double H)> Top { get; }

    /// <summary>"pyramid" (one apex) or "roof" (a ridge).</summary>
    public string Shape => Top.Count == 1 ? "pyramid" : "roof";

    /// <summary>The highest vertex above the wall, mm.</summary>
    public double TopHeightMm => Top.Count == 0 ? 0 : Top.Max(t => t.H);

    /// <summary>Flat sides: faces that lie in one plane (a roof's long side can be two triangles) count once.</summary>
    public int SideCount
    {
        get
        {
            var sides = new List<(double Na, double Nb, double Nh, double D)>();
            foreach (var p in planes)
            {
                if (!sides.Any(s => (s.Na * p.Na) + (s.Nb * p.Nb) + (s.Nh * p.Nh) > SameSideCos && Math.Abs(s.D - p.D) < 3))
                {
                    sides.Add(p);
                }
            }

            return sides.Count;
        }
    }

    /// <summary>The surface height at (a, b): the lowest face plane there, 0 outside the base.</summary>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <returns>Height above the facet, mm.</returns>
    public double HeightAt(double a, double b)
    {
        var (h, _) = Lowest(a, b);
        return Math.Max(0, h);
    }

    /// <summary>The outward unit normal of the face over (a, b), in facet coordinates; (0, 0, 1) on the wall.</summary>
    /// <param name="a">Along u, mm.</param>
    /// <param name="b">Along v, mm.</param>
    /// <returns>The normal.</returns>
    public double[] NormalAt(double a, double b)
    {
        var (h, k) = Lowest(a, b);
        return h <= 0 || k < 0 ? [0, 0, 1] : [planes[k].Na, planes[k].Nb, planes[k].Nh];
    }

    /// <summary>The storage form: per face its vertices as [a, b, h], rounded to 0.1 mm.</summary>
    /// <returns>The arrays.</returns>
    public double[][][] ToArrays() =>
        faces.Select(f => f.Select(v => new[] { Math.Round(v.A, 1), Math.Round(v.B, 1), Math.Round(v.H, 1) }).ToArray()).ToArray();

    /// <summary>Parses the storage form; null when malformed.</summary>
    /// <param name="arrays">The arrays.</param>
    /// <returns>The polyhedron or null.</returns>
    public static VolumePolyhedron? FromArrays(double[][][]? arrays)
    {
        if (arrays is null || arrays.Length < 3 || arrays.Length > 64
            || arrays.Any(f => f is null || f.Length < 3 || f.Any(v => v is null || v.Length != 3 || !v.All(double.IsFinite))))
        {
            return null;
        }

        var p = new VolumePolyhedron(arrays.Select(f => f.Select(v => (v[0], v[1], v[2])).ToArray()));
        return p.planes.Length >= 3 && p.Top.Count is 1 or 2 && p.Base.Count >= 3 ? p : null;
    }

    private (double H, int Face) Lowest(double a, double b)
    {
        var best = double.PositiveInfinity;
        var face = -1;
        for (var k = 0; k < planes.Length; k++)
        {
            var p = planes[k];
            var h = (p.D - (p.Na * a) - (p.Nb * b)) / p.Nh;
            if (h < best)
            {
                (best, face) = (h, k);
            }
        }

        return (face < 0 ? 0 : best, face);
    }

    /// <summary>The face's plane n·x = D with a unit normal pointing away from the wall (Newell's method).</summary>
    private static (double Na, double Nb, double Nh, double D) Plane((double A, double B, double H)[] f)
    {
        double na = 0, nb = 0, nh = 0;
        for (var i = 0; i < f.Length; i++)
        {
            var p = f[i];
            var q = f[(i + 1) % f.Length];
            na += (p.B - q.B) * (p.H + q.H);
            nb += (p.H - q.H) * (p.A + q.A);
            nh += (p.A - q.A) * (p.B + q.B);
        }

        var len = Math.Sqrt((na * na) + (nb * nb) + (nh * nh));
        if (len <= 0)
        {
            return (0, 0, 0, 0);
        }

        var sign = nh < 0 ? -1 : 1;
        (na, nb, nh) = (sign * na / len, sign * nb / len, sign * nh / len);
        var c = (f.Average(v => v.A), f.Average(v => v.B), f.Average(v => v.H));
        return (na, nb, nh, (na * c.Item1) + (nb * c.Item2) + (nh * c.Item3));
    }
}
