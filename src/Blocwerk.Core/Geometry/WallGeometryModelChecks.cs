// <copyright file="WallGeometryModelChecks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Geometry;

/// <summary>
/// What the solver says about a model (<c>quality.checks</c>, <c>quality.gravityDetail</c>,
/// <c>quality.rejectedObservations</c>, the measured segment angles) as plain-language lines for the wall admin.
/// Tolerant of older documents: whatever is missing is simply not reported.
/// </summary>
public static class WallGeometryModelChecks
{
    /// <summary>A declared angle this far (degrees) from the measured one is flagged.</summary>
    public const double AngleMismatchDeg = 3.0;

    /// <summary>A declared-level pair this far apart in height (mm) is flagged.</summary>
    public const double LevelMismatchMm = 10.0;

    /// <summary>A marker side RMS error above this (mm) is flagged.</summary>
    public const double MarkerSideRmsMm = 3.0;

    /// <summary>The checks of a stored model's JSON; empty when it is missing or no longer parses.</summary>
    public static IReadOnlyList<WallGeometryModelCheck> FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return From(WallGeometryDocument.Parse(json));
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The checks of a parsed document: angles first, then the solver's warnings, then the rest.</summary>
    public static IReadOnlyList<WallGeometryModelCheck> From(WallGeometryDocument document)
    {
        var quality = document.Quality;
        var checks = quality?.Checks;
        var list = new List<WallGeometryModelCheck>();
        if (document.World?.GravityKnown == false || string.Equals(quality?.Gravity, "unknown", StringComparison.Ordinal))
        {
            list.Add(new(WallGeometryModelCheck.KindGravity, WallGeometryModelCheck.Warning,
                "Gravity is unknown: no segment could serve as the vertical reference, so the wall's angles were not measured (distances are still valid)."));
        }

        list.AddRange(document.Segments.Select(SegmentAngle).OfType<WallGeometryModelCheck>());
        if (checks?.Warnings is { } warnings)
        {
            list.AddRange(warnings.Where(w => !string.IsNullOrWhiteSpace(w))
                .Select(w => new WallGeometryModelCheck(WallGeometryModelCheck.KindSolverWarning, WallGeometryModelCheck.Warning, w)));
        }
        else
        {
            list.AddRange(LegacyWarnings(document));
        }

        list.AddRange((checks?.LevelPairs ?? []).Select(LevelPair).OfType<WallGeometryModelCheck>());
        if (MarkerSize(checks) is { } size)
        {
            list.Add(size);
        }

        list.AddRange((quality?.RejectedObservations ?? []).Select(r => new WallGeometryModelCheck(
            WallGeometryModelCheck.KindRejectedObservation, WallGeometryModelCheck.Info, IgnoredDetectionFindings.Describe(r, r.Photo))));
        return list;
    }

    /// <summary>"45.2° overhang", "3.0° slab" or "vertical" (+ = overhang).</summary>
    public static string AngleText(double degrees)
    {
        var rounded = Math.Round(degrees, 1);
        if (rounded == 0)
        {
            return "vertical";
        }

        var value = Math.Abs(rounded).ToString("0.0", CultureInfo.InvariantCulture);
        return rounded > 0 ? $"{value}° overhang" : $"{value}° slab";
    }

    internal static string SegmentName(string? name, int index)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return $"Segment {index}";
        }

        var trimmed = name.Trim();
        return char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
    }

    private static WallGeometryModelCheck? SegmentAngle(WallGeometrySegment segment)
    {
        var measured = segment.Facets.Count <= 1
            ? MeasuredText(segment.MeasuredAngleDeg ?? segment.Facets.FirstOrDefault()?.MeasuredAngleDeg)
            : FacetsText(segment.Facets);
        if (measured is null)
        {
            return null;
        }

        var reference = segment.AngleIsGravityReference == true;
        var note = (segment.DeclaredAngleDeg, reference) switch
        {
            ({ } d, true) => $" (declared {DeclaredText(d)}, used as the vertical reference)",
            (null, true) => " (used as the vertical reference)",
            ({ } d, false) => $" (declared {DeclaredText(d)})",
            _ => string.Empty,
        };
        var off = !reference && segment.DeclaredVsMeasuredDeg is { } diff && Math.Abs(diff) >= AngleMismatchDeg;
        return new(WallGeometryModelCheck.KindSegmentAngle, off ? WallGeometryModelCheck.Warning : WallGeometryModelCheck.Info,
            $"{SegmentName(segment.Name, segment.Index)} {measured}{note}");
    }

    private static string? MeasuredText(double? degrees) => degrees is { } d ? AngleText(d) : null;

    private static string? FacetsText(IReadOnlyList<WallGeometryFacet> facets)
    {
        var parts = facets.Where(f => f.MeasuredAngleDeg is not null)
            .Select(f => $"facet {f.Id} {AngleText(f.MeasuredAngleDeg!.Value)}")
            .ToList();
        return parts.Count == 0 ? null : $"is folded: {string.Join(", ", parts)}";
    }

    private static string DeclaredText(double degrees) => degrees switch
    {
        0 => "vertical",
        > 0 => $"{degrees.ToString("0.#", CultureInfo.InvariantCulture)}°",
        _ => $"{(-degrees).ToString("0.#", CultureInfo.InvariantCulture)}° slab",
    };

    private static WallGeometryModelCheck? LevelPair(WallGeometryLevelPair pair)
    {
        if (pair.Pair.Length != 2 || pair.HeightDiffMm is not { } diff)
        {
            return null;
        }

        var mm = Math.Abs(diff).ToString("0.#", CultureInfo.InvariantCulture);
        return new(WallGeometryModelCheck.KindLevelPair,
            Math.Abs(diff) > LevelMismatchMm ? WallGeometryModelCheck.Warning : WallGeometryModelCheck.Info,
            $"Markers {pair.Pair[0]} and {pair.Pair[1]} (declared level) differ in height by {mm} mm.");
    }

    private static WallGeometryModelCheck? MarkerSize(WallGeometryQualityChecks? checks)
    {
        if (checks?.MarkerSideRmsErrMm is not { } rms)
        {
            return null;
        }

        var mean = checks.MarkerSideMeanErrMm ?? 0;
        var direction = mean >= 0 ? "larger" : "smaller";
        var text = $"The markers measure {Math.Abs(mean).ToString("0.0", CultureInfo.InvariantCulture)} mm {direction} than printed "
                   + $"on average (spread {rms.ToString("0.0", CultureInfo.InvariantCulture)} mm).";
        return new(WallGeometryModelCheck.KindMarkerSize,
            rms > MarkerSideRmsMm ? WallGeometryModelCheck.Warning : WallGeometryModelCheck.Info, text);
    }

    private static IEnumerable<WallGeometryModelCheck> LegacyWarnings(WallGeometryDocument document)
    {
        foreach (var split in document.Quality?.GravityDetail?.SplitReferences ?? [])
        {
            yield return new(WallGeometryModelCheck.KindSplitReference, WallGeometryModelCheck.Warning, WallGeometryCheckText.Split(split));
        }

        foreach (var decision in document.Quality?.Checks?.BorderlineFacetDecisions ?? [])
        {
            var name = document.Segments.FirstOrDefault(s => s.Index == decision.Segment)?.Name;
            yield return new(WallGeometryModelCheck.KindBorderlineFacet, WallGeometryModelCheck.Warning,
                WallGeometryCheckText.Borderline(decision, SegmentName(name, decision.Segment)));
        }
    }
}
