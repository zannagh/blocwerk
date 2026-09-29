// <copyright file="CaptureUnplannedMarkers.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Markers of the plan's dictionary that the plan does not list: stuck on the wall after it was printed (The Attic,
/// 2026-09-29: two more on the same sheets' family and size). With a plan, capture photos are decoded for every id of
/// the dictionary; an unplanned id is stored like any detection (the quiet-zone check still drops a hold read as an
/// id) and goes to the solver, without a planned segment or position, only when at least <see cref="MinPhotos"/>
/// photos decode it. The solver keeps it only when it lies on a solved surface and reprojects like the planned markers
/// (<c>docker/wall-geometry/wallgeometry/unplanned.py</c>); one seen in fewer photos is listed in the solver notes.
/// </summary>
public static class CaptureUnplannedMarkers
{
    /// <summary>Ids of DICT_4X4_50, the dictionary every plan prints from.</summary>
    public const int DictionarySize = 50;

    /// <summary>Photos that must decode an unplanned id before it goes to the solver (a hold misread rarely repeats).</summary>
    public const int MinPhotos = 3;

    /// <summary>The stored reason of an unplanned id seen in too few photos (solver notes only; never on a stored marker).</summary>
    public const string FewPhotosReason = Geometry.WallGeometryRejectedObservation.UnplannedFewPhotos;

    private static readonly IReadOnlySet<int> DictionaryIds = Enumerable.Range(0, DictionarySize).ToHashSet();

    /// <summary>Detection options of a capture photo: every dictionary id with a plan, the legacy ids without one.</summary>
    /// <param name="layout">The capture's marker layout.</param>
    public static MarkerDetectionOptions DetectionOptions(WallMarkerLayout layout) => layout.IsFromPlan
        ? layout.DetectionOptions with { AllowedIds = DictionaryIds }
        : layout.DetectionOptions;

    /// <summary>The unplanned ids at least <see cref="MinPhotos"/> photos decode (usable detections only); empty without a plan.</summary>
    /// <param name="layout">The capture's marker layout.</param>
    /// <param name="photos">The capture's photos.</param>
    public static IReadOnlySet<int> Consistent(WallMarkerLayout layout, IEnumerable<WallCapturePhoto> photos) =>
        PhotosPerId(layout, photos).Where(kv => kv.Value.Count >= MinPhotos).Select(kv => kv.Key).ToHashSet();

    /// <summary>Unplanned ids decoded in fewer than <see cref="MinPhotos"/> photos: (photo index, marker), for the solver notes.</summary>
    /// <param name="layout">The capture's marker layout.</param>
    /// <param name="photos">The capture's photos.</param>
    public static IEnumerable<(int PhotoIndex, CaptureMarker Marker)> TooFewPhotos(
        WallMarkerLayout layout, IEnumerable<WallCapturePhoto> photos) =>
        PhotosPerId(layout, photos)
            .Where(kv => kv.Value.Count < MinPhotos)
            .OrderBy(kv => kv.Key)
            .Select(kv => (kv.Value[0].PhotoIndex, kv.Value[0].Marker with
            {
                Ignored = FewPhotosReason,
                IgnoredDetail = $"not in the marker plan and decoded in {Photos(kv.Value.Count)} only; "
                                + $"it takes {MinPhotos} to count as a marker",
            }));

    /// <summary>
    /// The markers of one photo that may go to the solver: the planned ones (<see cref="CaptureComputeDocuments.UsableMarkers"/>)
    /// and the <paramref name="unplanned"/> ids.
    /// </summary>
    /// <param name="layout">The capture's marker layout.</param>
    /// <param name="markersJson">The photo's stored markers.</param>
    /// <param name="unplanned">The unplanned ids to include (<see cref="Consistent"/>).</param>
    public static IReadOnlyList<CaptureMarker> SolveMarkers(WallMarkerLayout layout, string? markersJson, IReadOnlySet<int> unplanned)
    {
        var markers = CaptureComputeDocuments.ParseMarkers(markersJson).Where(m => m.Ignored is null);
        return layout.IsFromPlan
            ? markers.Where(m => layout.AllowedIds.Contains(m.Id) || unplanned.Contains(m.Id)).ToList()
            : markers.ToList();
    }

    private static Dictionary<int, List<(int PhotoIndex, CaptureMarker Marker)>> PhotosPerId(
        WallMarkerLayout layout, IEnumerable<WallCapturePhoto> photos)
    {
        var byId = new Dictionary<int, List<(int, CaptureMarker)>>();
        if (!layout.IsFromPlan)
        {
            return byId;
        }

        foreach (var photo in photos.OrderBy(p => p.Index))
        {
            var unplanned = CaptureComputeDocuments.ParseMarkers(photo.MarkersJson)
                .Where(m => m.Ignored is null && !layout.AllowedIds.Contains(m.Id) && m.Id is >= 0 and < DictionarySize);
            foreach (var marker in unplanned)
            {
                (byId.TryGetValue(marker.Id, out var list) ? list : byId[marker.Id] = []).Add((photo.Index, marker));
            }
        }

        return byId;
    }

    private static string Photos(int n) => n == 1 ? "1 photo" : $"{n} photos";
}
