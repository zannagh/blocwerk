// <copyright file="MarkerPlanValidator.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// Checks a marker plan and explains every problem in plain language with a concrete fix. Errors make
/// the plan unusable (it can't be saved); warnings and tips (codes starting with <c>tip-</c>) are
/// advice. Every message comes from the capture-1 lessons: decodable size, markers fully in frame,
/// each corner in several photos, no edge-on surfaces.
/// </summary>
public static partial class MarkerPlanValidator
{
    /// <summary>Ranges a photo setup must stay within.</summary>
    public const double MinDistanceMm = 300;

    /// <summary>Largest plausible photo distance.</summary>
    public const double MaxDistanceMm = 20000;

    /// <summary>Checks <paramref name="plan"/> against the sizing targets of <paramref name="options"/>.</summary>
    public static IReadOnlyList<PlanIssue> Validate(MarkerPlan plan, MarkerGenerationOptions? options = null)
    {
        options ??= MarkerGenerationOptions.Default;
        var issues = new List<PlanIssue>();
        CheckHeader(plan, issues);
        var photoOk = CheckPhoto(plan.Photo, issues);
        CheckCamera(plan.Photo, issues);

        var layout = NetLayout.Compute(plan);
        issues.AddRange(layout.Issues);

        var segments = plan.Segments
            .Where(s => double.IsFinite(s.WidthMm) && double.IsFinite(s.HeightMm) && s.WidthMm > 0 && s.HeightMm > 0)
            .GroupBy(s => s.Index)
            .ToDictionary(g => g.Key, g => g.First());
        if (photoOk)
        {
            CheckGrazing(segments.Values, issues);
        }

        CheckMarkers(plan, segments, photoOk, options, issues);
        CheckSegmentCoverage(plan, segments, issues);
        if (photoOk)
        {
            CheckSharedEdges(plan, segments, issues);
        }

        CheckPrint(plan, issues);
        AddTips(plan, photoOk, issues);
        return issues;
    }

    private static void CheckHeader(MarkerPlan plan, List<PlanIssue> issues)
    {
        if (plan.SchemaVersion != MarkerPlan.CurrentSchemaVersion)
        {
            issues.Add(Error("schema-version", $"This plan is schema version {plan.SchemaVersion}; this Blocwerk understands version {MarkerPlan.CurrentSchemaVersion}."));
        }

        if (!string.Equals(plan.Dictionary, ArucoDict4X4.DictionaryName, StringComparison.Ordinal))
        {
            issues.Add(Error("dictionary", $"Markers must come from {ArucoDict4X4.DictionaryName} (the only dictionary the detector reads); this plan says \"{plan.Dictionary}\"."));
        }

        if (plan.Segments.Count == 0)
        {
            issues.Add(Error("no-segments", "Draw at least one surface of the wall."));
        }

        if (plan.Markers.Count > ArucoDict4X4.Count)
        {
            issues.Add(Error("too-many-markers", $"The plan has {plan.Markers.Count} markers but {ArucoDict4X4.DictionaryName} only has {ArucoDict4X4.Count} ids — use bigger spacing (photograph from further away), drop fillers, or split the wall into fewer surfaces."));
        }
    }

    private static bool CheckPhoto(PhotoSetup photo, List<PlanIssue> issues)
    {
        var ok = true;
        if (!(double.IsFinite(photo.DistanceMm) && photo.DistanceMm is >= MinDistanceMm and <= MaxDistanceMm))
        {
            issues.Add(Error("photo-distance", $"The photo distance must be between {MinDistanceMm / 1000:0.#} m and {MaxDistanceMm / 1000:0} m."));
            ok = false;
        }

        if (!(double.IsFinite(photo.HorizontalFovDeg) && photo.HorizontalFovDeg is >= 5 and <= 150))
        {
            issues.Add(Error("photo-fov", "The camera's field of view must be between 5° and 150° — pick your phone and lens if unsure."));
            ok = false;
        }

        if (photo.ImageLongEdgePx is < 640 or > 20000)
        {
            issues.Add(Error("photo-resolution", "The photo's long edge must be between 640 and 20000 px (a phone photo is about 4032)."));
            ok = false;
        }

        return ok;
    }

    private static void CheckGrazing(IEnumerable<PlanSegment> segments, List<PlanIssue> issues)
    {
        foreach (var s in segments.Where(MarkerSizing.IsGrazing))
        {
            issues.Add(new PlanIssue(
                PlanIssueSeverity.Warning,
                "grazing-surface",
                $"\"{s.Name}\" ({SurfaceAngle.Describe(s.OverhangDeg)}) is turned {MarkerSizing.ObliquenessDeg(s):0}° away from where you stand for the photos, so its markers look edge-on. Photograph this surface face-on ({FaceOnAdvice(s)}); its marker sizes assume that.",
                s.Index,
                null));
        }
    }

    /// <summary>How to get square-on to a grazing surface: the steeper of its two turns decides.</summary>
    private static string FaceOnAdvice(PlanSegment s)
    {
        var tiltDominates = Math.Abs(Math.Cos(s.OverhangDeg * Math.PI / 180)) < Math.Abs(Math.Cos(s.YawDeg * Math.PI / 180));
        return !tiltDominates ? "step in front of it"
            : s.OverhangDeg < 0 ? "a near-flat slab: shoot down onto it from above"
            : "a roof: shoot up at it from below";
    }

    private static PlanIssue Error(string code, string message, int? segment = null, int? marker = null) =>
        new(PlanIssueSeverity.Error, code, message, segment, marker);

    private static PlanIssue Warning(string code, string message, int? segment = null, int? marker = null) =>
        new(PlanIssueSeverity.Warning, code, message, segment, marker);
}
