// <copyright file="SegmentShapeEdge.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// One edge of a segment outline in the segment's own frame, oriented counter-clockwise
/// (<see cref="From"/> → <see cref="To"/>, the surface on the left).
/// </summary>
/// <param name="Edge">Which edge this is.</param>
/// <param name="From">Counter-clockwise start vertex.</param>
/// <param name="To">Counter-clockwise end vertex.</param>
public readonly record struct SegmentShapeEdge(SegmentEdge Edge, PlanVector From, PlanVector To)
{
    /// <summary>Gets the edge length in mm.</summary>
    public double Length => (To - From).Length;

    /// <summary>Gets the unit direction From → To.</summary>
    public PlanVector Direction => (To - From).Normalized();

    /// <summary>Gets the unit normal pointing into the surface.</summary>
    public PlanVector Inward => Direction.Left();

    /// <summary>
    /// Gets a value indicating whether <see cref="From"/> is the edge's "start" — its lower end, or its
    /// left end for a horizontal edge — which <see cref="PlanAttachment.OffsetMm"/> is measured from.
    /// </summary>
    public bool StartsAtFrom =>
        Math.Abs(From.Y - To.Y) > 1e-9 ? From.Y < To.Y : From.X <= To.X;

    /// <summary>Gets the start vertex (lower/left end).</summary>
    public PlanVector Start => StartsAtFrom ? From : To;

    /// <summary>Gets the end vertex (the other end).</summary>
    public PlanVector End => StartsAtFrom ? To : From;

    /// <summary>Distance from <paramref name="p"/> to the edge segment.</summary>
    public double DistanceTo(PlanVector p)
    {
        var d = To - From;
        var lengthSq = d.Dot(d);
        var t = lengthSq < 1e-12 ? 0 : Math.Clamp((p - From).Dot(d) / lengthSq, 0, 1);
        return (p - (From + (d * t))).Length;
    }
}
