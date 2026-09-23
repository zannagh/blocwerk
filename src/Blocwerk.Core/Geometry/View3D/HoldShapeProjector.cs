// <copyright file="HoldShapeProjector.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Geometry.View3D;

/// <summary>
/// Maps a hold's traced outline (<see cref="Hold.ShapePoints"/> + <see cref="Hold.ShapeHoles"/>, fractions
/// of the photo relative to the hold's X/Y) onto its facet in millimetres, vertex by vertex, relative to
/// the hold's stored plane centre. Holds without an outline become an ellipse of their mm size.
/// </summary>
public static class HoldShapeProjector
{
    /// <summary>Most vertices kept on an outer ring (~880 holds are drawn at once).</summary>
    public const int MaxOutlineVertices = 64;

    /// <summary>Most vertices kept on one hole ring.</summary>
    public const int MaxHoleVertices = 32;

    /// <summary>Most holes kept per hold.</summary>
    public const int MaxHoles = 8;

    /// <summary>Vertices of the stand-in ellipse of a hold without an outline.</summary>
    public const int CircleVertices = 24;

    /// <summary>Builds the facet-space shape of <paramref name="hold"/>.</summary>
    /// <param name="hold">The hold.</param>
    /// <param name="widthMm">Its size along u (measured or default), for the ellipse / approximate fallback.</param>
    /// <param name="heightMm">Its size along v.</param>
    /// <param name="mapping">Its photo → facet mapping, when one exists.</param>
    /// <returns>The shape, never null.</returns>
    public static Wall3DHoldShape Project(
        Hold hold, double widthMm, double heightMm, (PhotoToPlane Map, Wall3DShapeSource Source)? mapping)
    {
        if (hold.ShapePoints is not { Count: >= 3 } outline)
        {
            return Ellipse(widthMm, heightMm);
        }

        var holes = (hold.ShapeHoles ?? []).Where(r => r.Count >= 3).Take(MaxHoles).ToList();
        if (mapping is { } m && Mapped(hold, outline, holes, m.Map, m.Source) is { } mapped)
        {
            return mapped;
        }

        return Approximate(outline, holes, widthMm, heightMm);
    }

    /// <summary>An ellipse of the given diameters, centred on the hold.</summary>
    /// <param name="widthMm">Diameter along u.</param>
    /// <param name="heightMm">Diameter along v.</param>
    /// <returns>The ellipse shape.</returns>
    public static Wall3DHoldShape Ellipse(double widthMm, double heightMm)
    {
        var ring = Enumerable.Range(0, CircleVertices)
            .Select(i => 2 * Math.PI * i / CircleVertices)
            .Select(t => Round(widthMm / 2 * Math.Cos(t), heightMm / 2 * Math.Sin(t)))
            .ToList();
        return new Wall3DHoldShape(Wall3DShapeSource.Circle, ring, []);
    }

    /// <summary>Every vertex through the mapping, relative to the MAPPED centre; null when any vertex fails.</summary>
    private static Wall3DHoldShape? Mapped(
        Hold hold, List<ShapePoint> outline, List<List<ShapePoint>> holes, PhotoToPlane map, Wall3DShapeSource source)
    {
        var (ca, cb) = map(hold.X, hold.Y);
        if (!double.IsFinite(ca) || !double.IsFinite(cb))
        {
            return null;
        }

        List<(double A, double B)>? Ring(List<ShapePoint> ring)
        {
            var points = ring.Select(sp => map(hold.X + sp.Dx, hold.Y + sp.Dy)).ToList();
            return points.All(p => double.IsFinite(p.A) && double.IsFinite(p.B))
                ? points.Select(p => (p.A - ca, p.B - cb)).ToList()
                : null;
        }

        var outer = Ring(outline);
        if (outer is null)
        {
            return null;
        }

        var mappedHoles = holes.Select(Ring).Where(r => r is not null).Select(r => r!).ToList();
        return Finish(source, outer, mappedHoles);
    }

    /// <summary>
    /// No mapping: the outline's photo bounding box stretched to the hold's mm size (image y down → b up).
    /// Right in size, but blind to perspective within the hold.
    /// </summary>
    private static Wall3DHoldShape Approximate(List<ShapePoint> outline, List<List<ShapePoint>> holes, double widthMm, double heightMm)
    {
        var minX = outline.Min(p => p.Dx);
        var maxX = outline.Max(p => p.Dx);
        var minY = outline.Min(p => p.Dy);
        var maxY = outline.Max(p => p.Dy);
        var sx = maxX - minX > 1e-9 ? widthMm / (maxX - minX) : 0;
        var sy = maxY - minY > 1e-9 ? heightMm / (maxY - minY) : 0;
        if (sx == 0 || sy == 0)
        {
            return Ellipse(widthMm, heightMm);
        }

        List<(double A, double B)> Ring(List<ShapePoint> ring) =>
            ring.Select(p => (p.Dx * sx, -p.Dy * sy)).ToList();

        return Finish(Wall3DShapeSource.Approximate, Ring(outline), holes.Select(Ring).ToList());
    }

    private static Wall3DHoldShape Finish(
        Wall3DShapeSource source, List<(double A, double B)> outer, List<List<(double A, double B)>> holes)
    {
        var outline = PolygonSimplifier.Cap(outer, MaxOutlineVertices).Select(p => Round(p.A, p.B)).ToList();
        var holeRings = holes
            .Select(r => (IReadOnlyList<double[]>)PolygonSimplifier.Cap(r, MaxHoleVertices).Select(p => Round(p.A, p.B)).ToList())
            .Where(r => r.Count >= 3)
            .ToList();
        return new Wall3DHoldShape(source, outline, holeRings);
    }

    /// <summary>0.1 mm is far below what a phone shows, and keeps the JSON payload small.</summary>
    private static double[] Round(double a, double b) => [Math.Round(a, 1), Math.Round(b, 1)];
}
