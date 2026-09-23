// <copyright file="MarkerPlanJson.Checks.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Structural checks after parsing: every field present, every number finite and in a sane range. The
/// deeper plan checks (geometry, coverage, sizes) are <see cref="MarkerPlanValidator"/>'s job.
/// </summary>
public static partial class MarkerPlanJson
{
    /// <summary>Most segments a plan may describe.</summary>
    public const int MaxSegments = 200;

    /// <summary>Most markers a plan may list (the dictionary limit is a validation error, not a parse error).</summary>
    public const int MaxMarkers = 1000;

    private const double MaxLengthMm = 100_000;

    private static List<string> CheckShape(MarkerPlan plan)
    {
        var errors = new List<string>();
        if (plan.SchemaVersion < 1)
        {
            errors.Add("schemaVersion is missing.");
        }
        else if (plan.SchemaVersion > MarkerPlan.CurrentSchemaVersion)
        {
            errors.Add($"This plan uses schema version {plan.SchemaVersion}, made by a newer Blocwerk; this one reads version {MarkerPlan.CurrentSchemaVersion}.");
        }

        if (string.IsNullOrWhiteSpace(plan.Dictionary))
        {
            errors.Add("dictionary is missing.");
        }

        if (plan.Photo is null)
        {
            errors.Add("photo is missing.");
        }
        else
        {
            Range(errors, "photo.distanceMm", plan.Photo.DistanceMm, MarkerPlanValidator.MinDistanceMm, MarkerPlanValidator.MaxDistanceMm);
            Range(errors, "photo.horizontalFovDeg", plan.Photo.HorizontalFovDeg, 10, 150);
            Range(errors, "photo.imageLongEdgePx", plan.Photo.ImageLongEdgePx, 640, 20000);
        }

        if (plan.Segments is null || plan.Markers is null)
        {
            errors.Add("segments and markers must both be lists (they may be empty).");
            return errors;
        }

        if (plan.Segments.Count > MaxSegments || plan.Markers.Count > MaxMarkers)
        {
            errors.Add($"A plan may have at most {MaxSegments} segments and {MaxMarkers} markers.");
            return errors;
        }

        for (var i = 0; i < plan.Segments.Count; i++)
        {
            CheckSegment(errors, $"segments[{i}]", plan.Segments[i]);
        }

        for (var i = 0; i < plan.Markers.Count; i++)
        {
            CheckMarker(errors, $"markers[{i}]", plan.Markers[i]);
        }

        if (plan.Print?.MountingHoles is { } holes)
        {
            errors.AddRange(holes.Problems().Select(p => $"print.mountingHoles: {p}"));
        }

        return errors;
    }

    private static void CheckSegment(List<string> errors, string at, PlanSegment? s)
    {
        if (s is null)
        {
            errors.Add($"{at} is null.");
            return;
        }

        Range(errors, $"{at}.index", s.Index, 0, 10_000);
        Range(errors, $"{at}.widthMm", s.WidthMm, 1, MaxLengthMm);
        Range(errors, $"{at}.heightMm", s.HeightMm, 1, MaxLengthMm);
        Range(errors, $"{at}.overhangDeg", s.OverhangDeg, -90, 90);
        Range(errors, $"{at}.yawDeg", s.YawDeg, -180, 180);
        if (s.Name is null || s.Name.Length > 200)
        {
            errors.Add($"{at}.name must be text of at most 200 characters.");
        }

        if (s.AttachedTo is { } a)
        {
            Range(errors, $"{at}.attachedTo.parentIndex", a.ParentIndex, 0, 10_000);
            Range(errors, $"{at}.attachedTo.offsetMm", a.OffsetMm, -MaxLengthMm, MaxLengthMm);
        }
    }

    private static void CheckMarker(List<string> errors, string at, PlanMarker? m)
    {
        if (m is null)
        {
            errors.Add($"{at} is null.");
            return;
        }

        Range(errors, $"{at}.id", m.Id, 0, 10_000);
        Range(errors, $"{at}.segment", m.Segment, 0, 10_000);
        Range(errors, $"{at}.xMm", m.XMm, -MaxLengthMm, MaxLengthMm);
        Range(errors, $"{at}.yMm", m.YMm, -MaxLengthMm, MaxLengthMm);
        Range(errors, $"{at}.sizeMm", m.SizeMm, 10, 1000);
    }

    private static void Range(List<string> errors, string field, double value, double min, double max)
    {
        if (!double.IsFinite(value) || value < min || value > max)
        {
            errors.Add($"{field} must be between {min:0.###} and {max:0.###} (was {value:0.###}).");
        }
    }
}
