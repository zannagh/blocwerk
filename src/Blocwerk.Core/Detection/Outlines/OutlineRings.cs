// <copyright file="OutlineRings.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// One hold outline as ABSOLUTE normalized rings: the outer boundary and its interior holes (pockets). The bridge between
/// <see cref="Hold"/> storage (offsets around the centre) and <see cref="HoldOutlineBoolean"/>.
/// </summary>
/// <param name="Outer">The outer ring, normalized image coordinates.</param>
/// <param name="Holes">The interior rings, same convention.</param>
internal sealed record OutlineRings(IReadOnlyList<P2> Outer, IReadOnlyList<IReadOnlyList<P2>> Holes)
{
    /// <summary>What the editor draws for the hold: the traced polygon, or the circle when there is none.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>Its rings.</returns>
    public static OutlineRings Of(Hold hold)
    {
        var holes = hold.ShapePoints is { Count: >= 3 } && hold.ShapeHoles is { } rings
            ? rings.Where(r => r.Count >= 3).Select(r => (IReadOnlyList<P2>)r.Select(p => new P2(hold.X + p.Dx, hold.Y + p.Dy)).ToList()).ToList()
            : [];
        return new OutlineRings(HoldShapePolygon.Of(hold), holes);
    }

    /// <summary>The filled area (outer minus holes), in normalized units.</summary>
    public double Area => ShapeGeometry.Area(Outer) - Holes.Sum(h => ShapeGeometry.Area(h));

    /// <summary>The area centroid of the outer ring.</summary>
    public P2 Centroid
    {
        get
        {
            double a = ShapeGeometry.SignedArea(Outer);
            if (Math.Abs(a) < 1e-14)
            {
                return new P2(Outer.Average(p => p.X), Outer.Average(p => p.Y));
            }

            double cx = 0;
            double cy = 0;
            for (int i = 0, j = Outer.Count - 1; i < Outer.Count; j = i++)
            {
                double f = P2.Cross(Outer[j], Outer[i]);
                cx += (Outer[j].X + Outer[i].X) * f;
                cy += (Outer[j].Y + Outer[i].Y) * f;
            }

            return new P2(cx / (6 * a), cy / (6 * a));
        }
    }

    /// <summary>True when the point lies in the filled area (inside the outer ring and in no hole).</summary>
    /// <param name="p">The point.</param>
    /// <returns>Whether it is on the hold.</returns>
    public bool Covers(P2 p) => ShapeGeometry.Contains(Outer, p) && !Holes.Any(h => ShapeGeometry.Contains(h, p));

    /// <summary>
    /// Writes the rings onto a hold as a plain polygon: the centre moves to the outline's centroid, the radius becomes
    /// the outline's equivalent radius, the holes are kept and any spline control points are dropped (the polygon is
    /// no longer the fit of those points).
    /// </summary>
    /// <param name="hold">The tracked hold.</param>
    public void WriteTo(Hold hold)
    {
        var c = Centroid;
        hold.X = Math.Round(c.X, 5);
        hold.Y = Math.Round(c.Y, 5);
        hold.Radius = Math.Round(Math.Max(Math.Sqrt(Math.Max(Area, 0) / Math.PI), 0.002), 5);
        hold.ShapePoints = Offsets(Outer, hold.X, hold.Y);
        hold.ShapeControlPoints = null;
        hold.ShapeHoles = Holes.Count == 0 ? null : Holes.Select(h => Offsets(h, hold.X, hold.Y)).ToList();
        hold.OutlineSource = Enums.HoldOutlineSource.Manual;
    }

    private static List<ShapePoint> Offsets(IReadOnlyList<P2> ring, double x, double y) =>
        ring.Select(p => new ShapePoint { Dx = Math.Round(p.X - x, 5), Dy = Math.Round(p.Y - y, 5) }).ToList();
}
