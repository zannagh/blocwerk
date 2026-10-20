// <copyright file="HoldOutlineSplineCache.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Runtime.CompilerServices;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Detection.Outlines;

/// <summary>
/// Remembers, per hold, whether its stored outline still is the curve through its control points, so
/// <see cref="HoldOutlineSpline.CurrentControlPoints"/> does not redraw the whole curve on every call. The wall
/// editor asks several times per render (the handles, the option row, the smooth checkbox, the add-point gate).
/// </summary>
/// <remarks>
/// The lists are edited in place (a handle drag writes <see cref="ShapePoint.Dx"/> on the stored point), so the
/// answer is keyed on a copy of every value it was computed from, never on the list references: any change to
/// either list, however small, is a miss and the curve is computed again. Exact comparison, so a hit always gives
/// the answer the full computation would. Entries die with their hold.
/// </remarks>
internal static class HoldOutlineSplineCache
{
    private static readonly ConditionalWeakTable<Hold, SplineCacheEntry> Entries = [];

    [ThreadStatic]
    private static int computations;

    /// <summary>Gets how many full checks ran on this thread; tests read it to see the cache work.</summary>
    internal static int Computations => computations;

    /// <summary>Whether <paramref name="stored"/> is the curve through <paramref name="control"/>, from the cache when nothing changed.</summary>
    /// <param name="hold">The hold both lists belong to.</param>
    /// <param name="control">Its control points.</param>
    /// <param name="stored">Its stored outline.</param>
    /// <param name="compute">The full check, run on a miss.</param>
    /// <returns>The answer.</returns>
    public static bool IsCurve(Hold hold, IReadOnlyList<ShapePoint> control, IReadOnlyList<ShapePoint> stored, Func<bool> compute)
    {
        if (Entries.TryGetValue(hold, out var entry) && Matches(entry.Control, control) && Matches(entry.Stored, stored))
        {
            return entry.Same;
        }

        computations++;
        bool same = compute();
        Entries.AddOrUpdate(hold, new SplineCacheEntry(Flatten(control), Flatten(stored), same));
        return same;
    }

    private static double[] Flatten(IReadOnlyList<ShapePoint> points)
    {
        var values = new double[points.Count * 2];
        for (int i = 0; i < points.Count; i++)
        {
            values[2 * i] = points[i].Dx;
            values[(2 * i) + 1] = points[i].Dy;
        }

        return values;
    }

    private static bool Matches(double[] values, IReadOnlyList<ShapePoint> points)
    {
        if (values.Length != points.Count * 2)
        {
            return false;
        }

        for (int i = 0; i < points.Count; i++)
        {
            if (values[2 * i] != points[i].Dx || values[(2 * i) + 1] != points[i].Dy)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>One cached answer and the values it was computed from.</summary>
/// <param name="Control">The control points, flattened (dx, dy, dx, dy, ...).</param>
/// <param name="Stored">The stored outline, flattened.</param>
/// <param name="Same">Whether the outline was their curve.</param>
internal sealed record SplineCacheEntry(double[] Control, double[] Stored, bool Same);
