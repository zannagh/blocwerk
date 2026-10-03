// <copyright file="MarkerPlanRevisionModels.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>One saved revision of a wall's marker plan, for the planner's history list.</summary>
/// <param name="Revision">The revision number (1, 2, …).</param>
/// <param name="CreatedAt">When it was saved.</param>
/// <param name="CreatedBy">Who saved it (display name), when known.</param>
/// <param name="Markers">How many markers it plans.</param>
/// <param name="IsCurrent">True for the wall's current plan.</param>
/// <param name="UsedByActiveModel">True when the wall's active 3D model was solved with it.</param>
/// <param name="EffectiveFrom">When the owner marked its markers as put up on the wall; null while only planned.</param>
/// <param name="MeasuredByCapture">True when any 3D model was solved with it: its markers were on the wall then.</param>
public sealed record MarkerPlanRevisionInfo(
    int Revision,
    DateTimeOffset CreatedAt,
    string? CreatedBy,
    int Markers,
    bool IsCurrent,
    bool UsedByActiveModel,
    DateTimeOffset? EffectiveFrom = null,
    bool MeasuredByCapture = false)
{
    /// <summary>
    /// True when the markers are known to have been on the wall: marked as swapped, or measured by a capture.
    /// Otherwise the revision is "planned, not yet on the wall".
    /// </summary>
    public bool IsOnWall => EffectiveFrom is not null || MeasuredByCapture;
}

/// <summary>The markers the wall's active model measured: what the next capture is compared with.</summary>
/// <param name="Revision">The model's plan revision; null when it measured the legacy markers.</param>
/// <param name="Markers">Those markers as a plan reads them.</param>
public sealed record MarkerCaptureBaseline(int? Revision, IReadOnlyList<PlanMarker> Markers)
{
    /// <summary>What changed from this baseline to <paramref name="markers"/>, with the advice for the next capture.</summary>
    public MarkerPlanChanges CompareWith(IEnumerable<PlanMarker> markers) =>
        MarkerPlanChanges.Build(Revision, MarkerPlanDiff.Compare(Markers, markers));
}

/// <summary>What changed since the markers the active model measured, and what the next capture needs.</summary>
/// <param name="BaselineRevision">The active model's plan revision; null when it measured the legacy markers.</param>
/// <param name="BaselineLabel">Plain words for the baseline ("revision 2", "the measured markers (no plan yet)").</param>
/// <param name="Diff">The per-marker comparison, baseline → plan.</param>
/// <param name="Advice">What the next capture must do (empty when nothing changed).</param>
public sealed record MarkerPlanChanges(int? BaselineRevision, string BaselineLabel, MarkerPlanDiffResult Diff, IReadOnlyList<string> Advice)
{
    /// <summary>The least unchanged markers a capture needs to keep the wall's frame (see <c>WallFrameRegistration</c>).</summary>
    public const int MinUnchangedMarkers = 3;

    /// <summary>Builds the change summary with its advice.</summary>
    public static MarkerPlanChanges Build(int? baselineRevision, MarkerPlanDiffResult diff)
    {
        var label = baselineRevision is { } r ? $"revision {r}" : "the measured markers (before any plan)";
        return new MarkerPlanChanges(baselineRevision, label, diff, Advise(diff));
    }

    private static List<string> Advise(MarkerPlanDiffResult diff)
    {
        if (diff.IsEmpty)
        {
            return [];
        }

        var kept = diff.UnchangedIds.Count;
        var advice = new List<string>
        {
            "Photograph the changed areas, and keep at least 3 unchanged markers, spread out, visible in overlapping photos: "
            + "they tie the new model to the old one, so hold positions stay valid.",
        };
        if (kept < MinUnchangedMarkers)
        {
            advice.Add($"Only {kept} marker(s) keep their id, surface, size and place. With fewer than {MinUnchangedMarkers} the next "
                       + "capture cannot be tied to the current model and will not be activated automatically — change fewer markers at once.");
        }

        if (diff.Resized.Count > 0)
        {
            advice.Add("A resized marker that keeps its id counts as a new marker: its old detections are not reused.");
        }

        return advice;
    }
}
