// <copyright file="WallGeometryCheckText.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;

namespace Blocwerk.Core.Geometry;

/// <summary>
/// Sentences for a document that carries the structured checks but no <c>quality.checks.warnings</c>
/// (the solver's own wording, <c>refplanes.py</c>, is used whenever it is there).
/// </summary>
internal static class WallGeometryCheckText
{
    public static string Split(WallGeometrySplitReference split)
    {
        var name = WallGeometryModelChecks.SegmentName(split.Name, split.Segment);
        var pieces = split.Pieces.OrderByDescending(p => p.Share ?? 0).ToList();
        if (pieces.Count == 0)
        {
            return $"{name} is declared vertical but is not flat; gravity was taken from the whole segment's plane.";
        }

        var main = pieces[0];
        var others = string.Join("; ", pieces.Skip(1).Select(p => $"markers {Ids(p)} (facet {p.Facet})"));
        var fold = split.FoldDeg is { } f ? $" lie at {Deg(f, "0.0")} to" : " are at an angle to";
        var share = main.Share is { } s ? $" ({(s * 100).ToString("0", CultureInfo.InvariantCulture)} %)" : string.Empty;
        var text = $"{name} is declared vertical but is not flat: {others}{fold} markers {Ids(main)} (facet {main.Facet}). "
                   + $"Gravity was taken from the whole segment's plane, which follows mainly the better-supported part {Ids(main)}{share}.";
        if (pieces.All(p => p.LeanDeg is not null))
        {
            var leans = string.Join(", ", pieces.Select(p => $"facet {p.Facet} {p.LeanDeg!.Value.ToString("+0.0;-0.0;0.0", CultureInfo.InvariantCulture)}°"));
            text += $" Against that vertical the pieces lean {leans} (+ = overhang).";
        }

        return text;
    }

    public static string Borderline(WallGeometryBorderlineDecision decision, string name)
    {
        var verdict = decision.Kind == "split" ? "split it into facets" : "kept it as one facet";
        var threshold = decision.FoldDeg is { } f ? $" against the {Deg(f, "0.##")} fold threshold" : string.Empty;
        var parts = decision.MinPlaneAngleDeg is { } a ? $". Its parts differ by {Deg(a, "0.00")}{threshold}" : string.Empty;
        return $"{name}: the facet decision is borderline{parts}, so the solver {verdict}, "
               + "but one or two more or different photos can tip it the other way.";
    }

    private static string Ids(WallGeometrySplitPiece piece) => $"[{string.Join(", ", piece.MarkerIds)}]";

    private static string Deg(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture) + "°";
}
