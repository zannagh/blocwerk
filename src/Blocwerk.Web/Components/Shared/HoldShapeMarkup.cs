// <copyright file="HoldShapeMarkup.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text;
using Blocwerk.Core.Entities;

namespace Blocwerk.Web.Components.Shared;

/// <summary>
/// The pure geometry-to-SVG-attribute strings behind <see cref="HoldShape"/>: normalized 0..1 hold
/// coordinates in, 0-100 viewBox strings with two decimals out. Kept out of the component so the exact
/// markup (and the pocket-hold split between visual path and hit polygon) is unit-testable.
/// </summary>
public static class HoldShapeMarkup
{
    /// <summary>The <c>points</c> of the hold's OUTER ring — the polygon a solid hold draws, and the hit
    /// target a pocket hold is tapped on (so a tap in the hole still selects the hold).</summary>
    /// <param name="x">The hold's normalized centre X.</param>
    /// <param name="y">The hold's normalized centre Y.</param>
    /// <param name="outer">The outer ring as offsets from the centre.</param>
    /// <returns>The <c>points</c> attribute value.</returns>
    public static string PolygonPoints(double x, double y, IReadOnlyList<ShapePoint> outer) =>
        string.Join(" ", outer.Select(sp => F((x + sp.Dx) * 100) + "," + F((y + sp.Dy) * 100)));

    /// <summary>
    /// True when the hold should draw as a pocket: a real outer polygon (≥ 3 points) and at least one hole
    /// ring with ≥ 3 points. Anything else renders exactly as a solid hold.
    /// </summary>
    /// <param name="outer">The outer ring, or null.</param>
    /// <param name="holes">The hole rings, or null.</param>
    /// <returns>Whether the pocket markup applies.</returns>
    public static bool HasPocket(IReadOnlyList<ShapePoint>? outer, IReadOnlyList<IReadOnlyList<ShapePoint>>? holes) =>
        outer is { Count: >= 3 } && holes is not null && holes.Any(h => h is { Count: >= 3 });

    /// <summary>
    /// The <c>d</c> of the pocket hold's VISUAL path: the outer ring followed by every hole ring (≥ 3 points)
    /// as closed sub-paths. Drawn with <c>fill-rule="evenodd"</c>, the holes are cut out, so the wall shows
    /// through and the stroke outlines both the outer edge and every hole edge.
    /// </summary>
    /// <param name="x">The hold's normalized centre X.</param>
    /// <param name="y">The hold's normalized centre Y.</param>
    /// <param name="outer">The outer ring as offsets from the centre.</param>
    /// <param name="holes">The hole rings as offsets from the same centre.</param>
    /// <returns>The path data.</returns>
    public static string PocketPath(double x, double y, IReadOnlyList<ShapePoint> outer, IReadOnlyList<IReadOnlyList<ShapePoint>> holes)
    {
        var d = new StringBuilder();
        AppendRing(d, x, y, outer);
        foreach (var hole in holes.Where(h => h is { Count: >= 3 }))
        {
            d.Append(' ');
            AppendRing(d, x, y, hole);
        }

        return d.ToString();
    }

    /// <summary>Formats a viewBox coordinate the way every hold overlay does (two decimals, invariant).</summary>
    /// <param name="v">The value.</param>
    /// <returns>The formatted value.</returns>
    public static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

    private static void AppendRing(StringBuilder d, double x, double y, IReadOnlyList<ShapePoint> ring)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            d.Append(i == 0 ? "M" : " L").Append(F((x + ring[i].Dx) * 100)).Append(',').Append(F((y + ring[i].Dy) * 100));
        }

        d.Append(" Z");
    }
}
