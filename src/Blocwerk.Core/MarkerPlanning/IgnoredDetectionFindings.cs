// <copyright file="IgnoredDetectionFindings.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Geometry;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>The solver's rejected detections (<c>quality.rejectedObservations</c>) as placement findings.</summary>
public static class IgnoredDetectionFindings
{
    /// <summary>One finding per rejected detection, with whether it took the whole marker out of the model.</summary>
    /// <param name="layout">The wall's marker layout.</param>
    /// <param name="document">The solved geometry.</param>
    /// <param name="photoLabel">Solve-request photo name → the admin's name for it; null keeps the name.</param>
    public static IReadOnlyList<(MarkerPlacementFinding Finding, bool Dropped)> From(
        WallMarkerLayout layout,
        WallGeometryDocument document,
        Func<string, string>? photoLabel = null)
    {
        var rejected = document.Quality?.RejectedObservations;
        if (rejected is null || rejected.Count == 0)
        {
            return [];
        }

        return rejected
            .OrderBy(r => r.Id)
            .ThenBy(r => r.Photo, StringComparer.Ordinal)
            .Select(r => (Finding(layout, r, photoLabel?.Invoke(r.Photo) ?? r.Photo), r.MarkerDropped))
            .ToList();
    }

    /// <summary>The admin-facing sentence for one rejected detection.</summary>
    /// <param name="rejected">The rejected detection.</param>
    /// <param name="photo">The photo's name as the admin knows it.</param>
    public static string Describe(WallGeometryRejectedObservation rejected, string photo)
    {
        var head = $"Ignored marker {rejected.Id} in {photo}";
        return rejected.Reason == WallGeometryRejectedObservation.SingleViewMisfit || rejected.MarkerDropped
            ? $"{head}: no other photo shows it and it doesn't fit a flat square marker — probably a false detection (a hold or a shadow read as a marker)."
            : $"{head}: doesn't match where the other photos place it — probably a false detection.";
    }

    private static MarkerPlacementFinding Finding(WallMarkerLayout layout, WallGeometryRejectedObservation rejected, string photo)
    {
        int? segment = layout.Markers.TryGetValue(rejected.Id, out var planned) ? planned.Segment : null;
        return new MarkerPlacementFinding(
            MarkerPlacementIssue.IgnoredDetection, rejected.Id, segment, null, rejected.ResidualPx, Describe(rejected, photo));
    }
}
