// <copyright file="RegistrationRefusal.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Geometry.Registration;

/// <summary>
/// The admin-facing reason a registration was refused because the unchanged markers disagree: which markers
/// miss by how much, and what most likely causes it — a marker the new solve itself could not fit (wrong
/// printed size, sheet not flat), markers seen in too few photos, a poor solve overall, or a moved marker.
/// </summary>
internal static class RegistrationRefusal
{
    /// <summary>Markers seen in at most this many photos are called poorly measured.</summary>
    private const int FewPhotos = 2;

    /// <summary>A solve with a reprojection RMS above this (px) is called poor; good captures stay under 2 px.</summary>
    private const double PoorReprojectionPx = 2.5;

    private const int WorstShown = 3;

    /// <summary>Refused when more than this share of the used markers is flagged by the solver.</summary>
    private const double MaxFlaggedShare = 0.2;

    /// <summary>
    /// Why the new solve itself cannot be trusted, or null: the solver flagged (down-weighted) a marker whose size
    /// or place CHANGED in this revision — a misdeclared size corrupts the whole solve, whatever the fit says —
    /// or more than <see cref="MaxFlaggedShare"/> of the used markers.
    /// </summary>
    public static string? SolveIntegrity(WallGeometryDocument solved, IReadOnlyList<int> used, IReadOnlySet<int>? unchangedIds)
    {
        var details = solved.Quality?.DownweightedMarkers ?? new Dictionary<string, WallGeometryDownweightedMarker>();
        var flagged = details.Keys.Select(k => int.TryParse(k, out var id) ? id : -1).Where(id => id >= 0).ToHashSet();
        var changedFlagged = unchangedIds is null
            ? []
            : solved.Markers.Select(m => m.Id).Distinct().Where(id => flagged.Contains(id) && !unchangedIds.Contains(id)).Order().ToList();
        if (changedFlagged.Count > 0)
        {
            var each = changedFlagged.Select(id => DoesNotFit(id, solved.FindMarker(id)?.SizeMm, details[id.ToString()]));
            return string.Join(" ", each)
                   + " Changed marker(s) that do not fit distort the whole new model, so it cannot be activated: correct the size "
                   + "in the marker plan (or reprint the sheet at the planned size, or put it where the plan says) and capture again.";
        }

        var flaggedUsed = used.Where(flagged.Contains).Order().ToList();
        if (flaggedUsed.Count > MaxFlaggedShare * used.Count)
        {
            return $"The new solve could not fit {flaggedUsed.Count} of the {used.Count} unchanged markers ({string.Join(", ", flaggedUsed)}) "
                   + $"to the photos (more than {MaxFlaggedShare:P0}), so the model itself is unreliable: check those markers' printed "
                   + "sizes in the marker plan and that the sheets lie flat and were not moved, then capture again.";
        }

        return null;
    }

    /// <summary>The message for a fit whose residuals exceed the limits.</summary>
    public static string Disagreement(
        double rms,
        double plainRms,
        IReadOnlyDictionary<int, double> residuals,
        WallGeometryDocument solved,
        WallGeometryDocument reference)
    {
        var nNew = WallFrameRegistration.Observations(solved);
        var nRef = WallFrameRegistration.Observations(reference);
        int Photos(int id) => Math.Min(nNew.GetValueOrDefault(id, 1), nRef.GetValueOrDefault(id, 1));

        var worst = residuals.OrderByDescending(kv => kv.Value).Take(WorstShown).ToList();
        var parts = new List<string>
        {
            $"The unchanged markers disagree by {rms:0.0} mm between the active and the new model (weighted by how many photos "
            + $"show each; at most {WallFrameRegistration.MaxRmsMm:0} mm is accepted"
            + (plainRms > WallFrameRegistration.MaxPlainRmsMm
                ? $", and {plainRms:0.0} mm unweighted, at most {WallFrameRegistration.MaxPlainRmsMm:0} mm)."
                : ").")
            + " Worst: " + string.Join(", ", worst.Select(kv => $"marker {kv.Key} by {kv.Value:0} mm ({PhotoCount(Photos(kv.Key))})")) + ".",
        };

        var flagged = (solved.Quality?.DownweightedMarkers?.Keys ?? [])
            .Select(k => int.TryParse(k, out var id) ? id : -1)
            .Where(id => id >= 0)
            .Order()
            .ToList();
        if (flagged.Count > 0)
        {
            parts.Add($"The new solve itself could not fit marker(s) {string.Join(", ", flagged)} to the photos: check their printed "
                      + "size in the marker plan (a resized sheet that was not reprinted distorts the whole model) and that the sheets lie flat.");
        }

        var reproj = solved.Quality?.ReprojRmsPx;
        if (reproj > PoorReprojectionPx)
        {
            parts.Add($"The new solve is poor overall ({reproj:0.0} px reprojection error; good captures stay under 2 px): "
                      + "use sharp photos that show every marker whole, each marker in at least three photos.");
        }

        var sparse = worst.Where(kv => Photos(kv.Key) <= FewPhotos).Select(kv => kv.Key).Order().ToList();
        if (sparse.Count > 0)
        {
            parts.Add($"Marker(s) {string.Join(", ", sparse)} are in only 1–{FewPhotos} photos of one of the two captures and so poorly "
                      + "measured: add photos that show them from other angles.");
        }

        parts.Add("If a marker was moved, update the marker plan so it counts as changed.");
        return string.Join(" ", parts);
    }

    private static string DoesNotFit(int id, double? sizeMm, WallGeometryDownweightedMarker d)
    {
        var size = sizeMm is { } mm ? $"was planned at {mm:0} mm but does not fit the photos as {mm:0} mm" : "does not fit the photos";
        var px = d.FreeRmsPx is { } free && d.MedianRmsPx is { } median
            ? $" (it misses by {free:0.0} px where a typical marker misses by {median:0.0} px)"
            : string.Empty;
        return $"Marker {id} {size}{px}; check its printed size.";
    }

    private static string PhotoCount(int n) => n == 1 ? "1 photo" : $"{n} photos";
}
