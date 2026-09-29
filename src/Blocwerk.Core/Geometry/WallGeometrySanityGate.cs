// <copyright file="WallGeometrySanityGate.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json;
using Blocwerk.Core.Geometry.Footprints;

namespace Blocwerk.Core.Geometry;

/// <summary>
/// The last line of defence before a solved marker model goes live: a model that measures its own printed markers
/// metres off, puts a camera kilometres away, tilts a surface far from what the plan/admin declared, or reprojects
/// badly is not activated (it is stored, with the reason). The Attic, 2026-09-29: false marker decodes produced a
/// model with the main wall at 17° slab (declared 45° overhang), marker sizes off by 52 m on average, a camera
/// 881 km away and 6.0 px reprojection; it was activated and broke textures, holds and the photo-real view.
/// Good solves of the same wall: angles within 4°, sizes within 1 mm (mean), cameras within 6 m, 2.9–3.1 px.
/// </summary>
public static class WallGeometrySanityGate
{
    /// <summary>A camera centre farther than this (mm) from the nearest marker is implausible.</summary>
    public const double MaxCameraDistanceMm = 50_000;

    /// <summary>The markers' mean measured side may be off by this share of the printed size (5 %).</summary>
    public const double MaxMarkerSizeMeanShare = 0.05;

    /// <summary>... and their spread (RMS) by this share (8 %: 10 mm on a 125 mm marker).</summary>
    public const double MaxMarkerSizeRmsShare = 0.08;

    /// <summary>A facet measured this far (degrees) from its segment's declared angle is implausible.</summary>
    public const double MaxAngleOffDeg = 10;

    /// <summary>
    /// Reprojection RMS (px) above this is implausible. Good solves of The Attic's 142 marker photos measure
    /// 2.9–3.1 px (phone photos, 80–125 mm markers), so the line sits at 4 px, above them and below the 6.0 px of
    /// the broken one.
    /// </summary>
    public const double MaxReprojRmsPx = 4.0;

    /// <summary>The problems of a solved marker model's JSON (empty: plausible, or not a marker model).</summary>
    /// <param name="json">The model JSON.</param>
    public static IReadOnlyList<string> Problems(string json)
    {
        WallGeometryDocument doc;
        try
        {
            doc = WallGeometryDocument.Parse(json);
        }
        catch (JsonException)
        {
            return [];
        }

        if (doc.IsFeatureFrame || doc.Markers.Count == 0)
        {
            return [];
        }

        var problems = new List<string>();
        problems.AddRange(MarkerSize(doc));
        problems.AddRange(Angles(doc));
        problems.AddRange(Cameras(doc, json));
        if (doc.Quality?.ReprojRmsPx is { } rms && rms > MaxReprojRmsPx)
        {
            problems.Add($"its reprojection error is {F(rms, "0.0")} px (at most {F(MaxReprojRmsPx, "0.0")} px is plausible)");
        }

        return problems;
    }

    /// <summary>The admin-facing sentence for a refused model.</summary>
    /// <param name="problems">The problems (<see cref="Problems"/>).</param>
    public static string Describe(IReadOnlyList<string> problems) =>
        "The solved model failed the sanity checks: " + string.Join("; ", problems) + ".";

    private static IEnumerable<string> MarkerSize(WallGeometryDocument doc)
    {
        var checks = doc.Quality?.Checks;
        var size = doc.MarkerSizeMm > 0 ? doc.MarkerSizeMm : 125;
        if (checks?.MarkerSideMeanErrMm is { } mean && Math.Abs(mean) > MaxMarkerSizeMeanShare * size)
        {
            yield return $"its markers measure {F(mean, "+0.0;-0.0")} mm off their printed size on average "
                         + $"(at most {F(MaxMarkerSizeMeanShare * size, "0.0")} mm is plausible)";
        }
        else if (checks?.MarkerSideRmsErrMm is { } spread && spread > MaxMarkerSizeRmsShare * size)
        {
            yield return $"its marker sizes spread by {F(spread, "0.0")} mm (at most {F(MaxMarkerSizeRmsShare * size, "0.0")} mm is plausible)";
        }
    }

    private static IEnumerable<string> Angles(WallGeometryDocument doc)
    {
        if (doc.World?.GravityKnown == false)
        {
            yield break;
        }

        foreach (var segment in doc.Segments)
        {
            var declared = segment.DeclaredAngleDeg ?? (segment.AngleIsGravityReference == true ? 0 : null);
            if (declared is null)
            {
                continue;
            }

            foreach (var facet in segment.Facets.Where(f => f.MeasuredAngleDeg is { } a && Math.Abs(a - declared.Value) > MaxAngleOffDeg))
            {
                yield return $"{WallGeometryModelChecks.SegmentName(segment.Name, segment.Index)} (facet {facet.Id}) measures "
                             + $"{WallGeometryModelChecks.AngleText(facet.MeasuredAngleDeg!.Value)}, declared "
                             + $"{WallGeometryModelChecks.AngleText(declared.Value)}";
            }
        }
    }

    private static IEnumerable<string> Cameras(WallGeometryDocument doc, string json)
    {
        var markers = doc.Markers
            .Where(m => m.CornersWorldMm is { Count: > 0 })
            .Select(m => Enumerable.Range(0, 3).Select(i => m.CornersWorldMm!.Average(c => c[i])).ToArray())
            .ToList();
        if (markers.Count == 0)
        {
            yield break;
        }

        var far = SolvedCamera.ParseAll(json)
            .Select(c => (c.Image, Distance: markers.Min(m => Math.Sqrt(c.Centre.Zip(m, (a, b) => (a - b) * (a - b)).Sum()))))
            .Where(c => !(c.Distance <= MaxCameraDistanceMm))
            .OrderByDescending(c => c.Distance)
            .ToList();
        if (far.Count > 0)
        {
            var worst = double.IsFinite(far[0].Distance) ? $"{F(far[0].Distance / 1000, "0.#")} m" : "infinitely far";
            yield return $"{far.Count} camera(s) sit implausibly far from the wall ({far[0].Image}: {worst}; at most "
                         + $"{F(MaxCameraDistanceMm / 1000, "0")} m is plausible)";
        }
    }

    private static string F(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
