// <copyright file="HoldShapeLook.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Contracts.Documents;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Blocwerk.Shared.Ui.Overlay;

/// <summary>Every scalar <see cref="HoldShape"/> parameter, compared by value to decide whether it must re-render.</summary>
internal readonly record struct HoldShapeLook(
    Guid? HoldId,
    string? StackKeys,
    double X,
    double Y,
    double Radius,
    bool Foot,
    double FootScale,
    string? Fill,
    string? Stroke,
    string? StrokeWidth,
    string? Dash,
    bool Kept,
    string? PointerEvents,
    string? Style,
    EventCallback<MouseEventArgs> OnClick,
    EventCallback<PointerEventArgs> OnPointerDown,
    EventCallback<MouseEventArgs> OnMouseOver,
    EventCallback<MouseEventArgs> OnMouseOut)
{
    private const double NoList = double.NaN;

    /// <summary>The outline and hole rings as one flat list of numbers: counts (NaN for none) and coordinates.</summary>
    /// <param name="outline">The outer ring, or null.</param>
    /// <param name="holes">The hole rings, or null.</param>
    /// <returns>The flat list.</returns>
    public static double[] Geometry(IReadOnlyList<DocumentPoint>? outline, IReadOnlyList<IReadOnlyList<DocumentPoint>>? holes)
    {
        var values = new List<double>();
        Append(values, outline);
        values.Add(holes?.Count ?? NoList);
        foreach (var hole in holes ?? [])
        {
            Append(values, hole);
        }

        return [.. values];
    }

    /// <summary>Whether the rings still hold exactly the values <paramref name="geometry"/> was made from.</summary>
    /// <param name="geometry">A list from <see cref="Geometry"/>.</param>
    /// <param name="outline">The outer ring now.</param>
    /// <param name="holes">The hole rings now.</param>
    /// <returns>True when nothing changed.</returns>
    public static bool SameGeometry(double[] geometry, IReadOnlyList<DocumentPoint>? outline, IReadOnlyList<IReadOnlyList<DocumentPoint>>? holes)
    {
        int at = 0;
        if (!Matches(geometry, ref at, outline) || at >= geometry.Length || !SameCount(geometry[at++], holes?.Count))
        {
            return false;
        }

        foreach (var hole in holes ?? [])
        {
            if (!Matches(geometry, ref at, hole))
            {
                return false;
            }
        }

        return at == geometry.Length;
    }

    private static void Append(List<double> values, IReadOnlyList<DocumentPoint>? ring)
    {
        values.Add(ring?.Count ?? NoList);
        foreach (var p in ring ?? [])
        {
            values.Add(p.Dx);
            values.Add(p.Dy);
        }
    }

    private static bool Matches(double[] geometry, ref int at, IReadOnlyList<DocumentPoint>? ring)
    {
        if (at >= geometry.Length || !SameCount(geometry[at++], ring?.Count))
        {
            return false;
        }

        foreach (var p in ring ?? [])
        {
            if (at + 1 >= geometry.Length || geometry[at] != p.Dx || geometry[at + 1] != p.Dy)
            {
                return false;
            }

            at += 2;
        }

        return true;
    }

    private static bool SameCount(double stored, int? count) => count is { } n ? stored == n : double.IsNaN(stored);
}
