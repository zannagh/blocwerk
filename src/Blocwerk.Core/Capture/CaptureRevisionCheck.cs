// <copyright file="CaptureRevisionCheck.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Refuses a capture whose photos show another marker revision than the one it was solved with — e.g. the
/// plan was saved with 60 mm fillers but the old 125 mm sheets are still up. The solve's measured sizes
/// (<see cref="WallGeometryMarker.MeasuredSideMm"/>, judged by <see cref="MarkerSizeCheck"/>) are the evidence,
/// read by <see cref="MarkerRevisionInference.InferFromMeasuredSizes"/> over the whole photo set.
/// </summary>
public static class CaptureRevisionCheck
{
    /// <summary>
    /// Why <paramref name="solved"/> (solved as <paramref name="usedRevision"/>) cannot be used, or null when the
    /// photos are compatible with that revision (or show too little to tell).
    /// </summary>
    public static string? Refusal(
        WallGeometryDocument solved, int usedRevision, IReadOnlyList<RevisionCandidate> candidates, DateTimeOffset at)
    {
        var measured = solved.Markers
            .Where(m => m.MeasuredSideMm is > 0)
            .GroupBy(m => m.Id)
            .ToDictionary(g => g.Key, g => g.First().MeasuredSideMm!.Value);
        var evidence = MarkerRevisionInference.InferFromMeasuredSizes(measured, candidates, at);
        if (evidence is null || !evidence.Costs.TryGetValue(usedRevision, out var used)
            || used <= evidence.Costs.Values.Min() + MarkerRevisionInference.Margin)
        {
            return null;
        }

        var shown = evidence.Costs.Where(kv => kv.Key != usedRevision).MinBy(kv => kv.Value).Key;
        var byRevision = candidates.ToDictionary(c => c.Revision, c => c.Plan.Markers.ToDictionary(m => m.Id, m => m.SizeMm));
        var reasons = evidence.DecidingIds
            .Where(measured.ContainsKey)
            .Select(id => Reason(id, measured[id], usedRevision, byRevision[usedRevision], shown, byRevision[shown]))
            .OfType<string>()
            .ToList();
        return string.Create(
            CultureInfo.InvariantCulture,
            $"The photos show the markers of plan revision {shown}, but this capture uses revision {usedRevision}: {string.Join("; ", reasons)}. ")
            + $"Solving with the wrong sizes would distort the whole model, so it was not used. Put up revision {usedRevision}'s "
            + $"markers (and mark them as swapped in the marker planner) and capture again, or use revision {shown} for this capture.";
    }

    private static string? Reason(
        int id, double measuredMm, int used, IReadOnlyDictionary<int, double> usedSizes, int shown, IReadOnlyDictionary<int, double> shownSizes)
    {
        var inShown = shownSizes.TryGetValue(id, out var shownMm) ? $"revision {shown}: {shownMm:0} mm" : $"not in revision {shown}";
        if (!usedSizes.TryGetValue(id, out var usedMm))
        {
            return string.Create(CultureInfo.InvariantCulture, $"marker {id} is not in revision {used} ({inShown})");
        }

        return MarkerSizeCheck.Mismatch(usedMm, measuredMm) is null
            ? null
            : string.Create(
                CultureInfo.InvariantCulture,
                $"marker {id} measures ≈{measuredMm:0} mm where revision {used} plans {usedMm:0} mm ({inShown})");
    }
}
