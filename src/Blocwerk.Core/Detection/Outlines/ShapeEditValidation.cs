// <copyright file="ShapeEditValidation.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// The server's check on an outline the wall editor's Shape tool drew in the browser (a dragged handle, an
/// eraser stroke, a brush stroke): the browser previews and proposes, this decides what may be stored. An
/// outline it refuses leaves the hold as it was.
/// </summary>
public static class ShapeEditValidation
{
    /// <summary>The most outline points an edit may send; a brush stroke is simplified well below it.</summary>
    public const int MaxOutlinePoints = 400;

    /// <summary>How far (in the photo's 0..1 space) an outline may reach past the photo's edge.</summary>
    public const double EdgeSlack = 0.01;

    /// <summary>
    /// Why the outline cannot be stored, or null when it can.
    /// </summary>
    /// <param name="x">The hold's centre X (0..1).</param>
    /// <param name="y">The hold's centre Y (0..1).</param>
    /// <param name="points">The outline (or control points) as offsets from the centre.</param>
    /// <param name="control">True when <paramref name="points"/> are the control points of a smooth outline.</param>
    /// <param name="requireSimple">True when the outline must not cross itself (the eraser and the brush).</param>
    /// <returns>The reason, in words for the editor, or null.</returns>
    public static string? Problem(double x, double y, IReadOnlyList<ShapePoint> points, bool control, bool requireSimple)
    {
        if (points.Count < 3)
        {
            return "An outline needs at least three points.";
        }

        int max = control ? HoldOutlineSpline.MaxEditableControlPoints : MaxOutlinePoints;
        if (points.Count > max)
        {
            return $"An outline can have at most {max} points.";
        }

        if (points.Any(p => !double.IsFinite(p.Dx) || !double.IsFinite(p.Dy)))
        {
            return "The outline has an invalid point.";
        }

        var outline = control ? HoldOutlineSpline.Densify(points) : [.. points];
        return OutlineProblem(x, y, outline, requireSimple);
    }

    private static string? OutlineProblem(double x, double y, List<ShapePoint> outline, bool requireSimple)
    {
        var poly = outline.Select(p => new P2(x + p.Dx, y + p.Dy)).ToList();
        if (poly.Any(p => p.X < -EdgeSlack || p.X > 1 + EdgeSlack || p.Y < -EdgeSlack || p.Y > 1 + EdgeSlack))
        {
            return "The outline must stay on the photo.";
        }

        if (ShapeGeometry.Area(poly) < 1e-9)
        {
            return "The outline has no area left.";
        }

        if (requireSimple && !ShapeGeometry.IsSimple(poly))
        {
            return "The outline would cross itself.";
        }

        return null;
    }
}
