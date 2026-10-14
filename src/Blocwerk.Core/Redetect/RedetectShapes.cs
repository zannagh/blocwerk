// <copyright file="RedetectShapes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Redetect;

/// <summary>
/// Writes a detection's shape onto the stored hold it was matched to. Only geometry and what is measured from it
/// changes: the id, name, colour, material, flags, boulders and links of the stored hold stay.
/// </summary>
public static class RedetectShapes
{
    /// <summary>Applies one decided change.</summary>
    /// <param name="hold">The stored hold.</param>
    /// <param name="fresh">The detection.</param>
    /// <param name="change">What the hold takes.</param>
    public static void Apply(Hold hold, Hold fresh, RedetectShapeChange change)
    {
        ArgumentNullException.ThrowIfNull(hold);
        ArgumentNullException.ThrowIfNull(fresh);
        if (change == RedetectShapeChange.FillOutline)
        {
            FillOutline(hold, fresh);
        }
        else
        {
            Replace(hold, fresh);
        }
    }

    /// <summary>The stored hold takes the detection's position, radius and outline, and the measurements that go with them.</summary>
    private static void Replace(Hold hold, Hold fresh)
    {
        hold.X = fresh.X;
        hold.Y = fresh.Y;
        hold.Radius = fresh.Radius;
        hold.ShapePoints = fresh.ShapePoints?.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy }).ToList();
        hold.ShapeHoles = ShapePoint.CloneRings(fresh.ShapeHoles);
        hold.ShapeControlPoints = null;
        hold.OutlineSource = fresh.OutlineSource;
        hold.OutlineConfidence = fresh.OutlineConfidence;
        hold.Confidence = Math.Max(hold.Confidence, fresh.Confidence);

        // Measured from the old outline: taken from the detection when it was measured, else unknown until measured again.
        hold.InvalidateGlyphPosition();
        hold.InvalidateGlyphSize();

        // The 3D footprint was traced from the old outline. It stays until the refine that follows has a new one: a
        // hold the refine cannot solve keeps the shape it had instead of losing it.
        hold.FingerprintJson = fresh.FingerprintJson ?? hold.FingerprintJson;
        if (fresh.MetricSource is not null)
        {
            (hold.WidthMm, hold.HeightMm, hold.AreaMm2) = (fresh.WidthMm, fresh.HeightMm, fresh.AreaMm2);
            (hold.FacetId, hold.PlaneAMm, hold.PlaneBMm, hold.MetricSource) = (fresh.FacetId, fresh.PlaneAMm, fresh.PlaneBMm, fresh.MetricSource);
        }
    }

    /// <summary>
    /// A hold a person placed keeps its position and radius and gets the outline it never had. The outline is stored
    /// relative to the hold's own centre, so it lies where the detection traced it.
    /// </summary>
    private static void FillOutline(Hold hold, Hold fresh)
    {
        var (dx, dy) = (fresh.X - hold.X, fresh.Y - hold.Y);
        hold.ShapePoints = fresh.ShapePoints?.Select(p => new ShapePoint { Dx = p.Dx + dx, Dy = p.Dy + dy }).ToList();
        hold.ShapeHoles = fresh.ShapeHoles?
            .Select(ring => ring.Select(p => new ShapePoint { Dx = p.Dx + dx, Dy = p.Dy + dy }).ToList())
            .ToList();
        hold.OutlineConfidence = fresh.OutlineConfidence;
        hold.FingerprintJson ??= fresh.FingerprintJson;
    }
}
