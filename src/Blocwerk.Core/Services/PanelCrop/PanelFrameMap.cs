// <copyright file="PanelFrameMap.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services.PanelCrop;

/// <summary>
/// Maps normalized coordinates of one photo frame onto another frame of the SAME pixels (a crop or its undo): an
/// axis-aligned scale and offset per axis, <c>x' = ScaleX * x + OffsetX</c>. A hold mapped through it keeps its spot on
/// the wall: only the frame its numbers are measured in changes.
/// </summary>
/// <param name="ScaleX">Scale on x.</param>
/// <param name="ScaleY">Scale on y.</param>
/// <param name="OffsetX">Offset on x.</param>
/// <param name="OffsetY">Offset on y.</param>
public readonly record struct PanelFrameMap(double ScaleX, double ScaleY, double OffsetX, double OffsetY)
{
    /// <summary>The map from a frame into the crop <paramref name="rect"/> of it.</summary>
    /// <param name="rect">The crop, relative to the source frame.</param>
    /// <returns>The map.</returns>
    public static PanelFrameMap IntoCrop(PanelCropRect rect) =>
        new(1 / rect.Width, 1 / rect.Height, -rect.Left / rect.Width, -rect.Top / rect.Height);

    /// <summary>The map from the crop <paramref name="rect"/> back out to the frame it was cut from.</summary>
    /// <param name="rect">The crop, relative to the target frame.</param>
    /// <returns>The map.</returns>
    public static PanelFrameMap OutOfCrop(PanelCropRect rect) => new(rect.Width, rect.Height, rect.Left, rect.Top);

    /// <summary>This map followed by <paramref name="next"/>.</summary>
    /// <param name="next">The map applied second.</param>
    /// <returns>The composed map.</returns>
    public PanelFrameMap Then(PanelFrameMap next) => new(
        next.ScaleX * ScaleX,
        next.ScaleY * ScaleY,
        (next.ScaleX * OffsetX) + next.OffsetX,
        (next.ScaleY * OffsetY) + next.OffsetY);

    /// <summary>Maps a point.</summary>
    /// <param name="x">Normalized x.</param>
    /// <param name="y">Normalized y.</param>
    /// <returns>The mapped point.</returns>
    public (double X, double Y) Point(double x, double y) => ((ScaleX * x) + OffsetX, (ScaleY * y) + OffsetY);

    /// <summary>
    /// Maps a hold's centre, outline (centre-relative offsets scale per axis) and pocket holes in place. The radius is a
    /// single number drawn on both axes, so it scales by the geometric mean of the two axis scales, which keeps its area.
    /// Nothing metric changes: the hold did not move on the wall, so its facet position and sizes stay valid.
    /// </summary>
    /// <param name="hold">The hold to rewrite.</param>
    public void Apply(Hold hold)
    {
        (hold.X, hold.Y) = Point(hold.X, hold.Y);
        hold.Radius *= Math.Sqrt(ScaleX * ScaleY);
        hold.ShapePoints = Offsets(hold.ShapePoints);
        var map = this;
        hold.ShapeHoles = hold.ShapeHoles?.Select(ring => map.Offsets(ring)!).ToList();
    }

    /// <summary>Maps a hold's geometry onto a detached copy (for previews), leaving the hold untouched.</summary>
    /// <param name="hold">The hold.</param>
    /// <returns>The mapped copy.</returns>
    public Hold Mapped(Hold hold)
    {
        var copy = hold.Clone();
        Apply(copy);
        return copy;
    }

    /// <summary>Scales centre-relative offsets (outline points) per axis.</summary>
    private List<ShapePoint>? Offsets(List<ShapePoint>? points)
    {
        var (sx, sy) = (ScaleX, ScaleY);
        return points?.Select(p => new ShapePoint { Dx = p.Dx * sx, Dy = p.Dy * sy }).ToList();
    }
}
