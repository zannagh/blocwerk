// <copyright file="PlanVector.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>A 2D point or direction in millimetres (a segment frame or the net frame, y up).</summary>
/// <param name="X">Horizontal coordinate.</param>
/// <param name="Y">Vertical coordinate (up the surface).</param>
public readonly record struct PlanVector(double X, double Y)
{
    /// <summary>Gets the Euclidean length.</summary>
    public double Length => Math.Sqrt((X * X) + (Y * Y));

    public static PlanVector operator +(PlanVector a, PlanVector b) => new(a.X + b.X, a.Y + b.Y);

    public static PlanVector operator -(PlanVector a, PlanVector b) => new(a.X - b.X, a.Y - b.Y);

    public static PlanVector operator *(PlanVector a, double k) => new(a.X * k, a.Y * k);

    /// <summary>Dot product.</summary>
    public double Dot(PlanVector other) => (X * other.X) + (Y * other.Y);

    /// <summary>The same direction with length 1 (or zero for a zero vector).</summary>
    public PlanVector Normalized()
    {
        var length = Length;
        return length < 1e-12 ? default : new PlanVector(X / length, Y / length);
    }

    /// <summary>This vector turned counter-clockwise by <paramref name="radians"/>.</summary>
    public PlanVector Rotate(double radians)
    {
        var cos = Math.Cos(radians);
        var sin = Math.Sin(radians);
        return new PlanVector((X * cos) - (Y * sin), (X * sin) + (Y * cos));
    }

    /// <summary>The counter-clockwise perpendicular (the interior side of a counter-clockwise edge).</summary>
    public PlanVector Left() => new(-Y, X);

    /// <summary>Angle of the vector in radians, counter-clockwise from +x.</summary>
    public double Angle() => Math.Atan2(Y, X);

    /// <summary>As a two-element array (the JSON/NetGeometry form).</summary>
    public double[] ToArray() => [X, Y];
}
