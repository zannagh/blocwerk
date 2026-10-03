// <copyright file="CaptureIgnoredDetectionsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// Detections the app ignored before the solve (no white border, not fitting the plan) are stored flagged, never reach
/// the solver, keep their photo in the capture, and show up in the solver notes with their reason.
/// </summary>
public class CaptureIgnoredDetectionsTests
{
    [Fact]
    public void IgnoredDetections_NeverReachTheSolver_ButThePhotoStays()
    {
        var photos = new List<WallCapturePhoto> { Photo(1, Real(3), Real(25), Hold(17, CapturePlanLayoutCheck.Reason)), Photo(2, Hold(17, CaptureMarkerDetection.NoQuietZoneReason)) };

        Assert.Equal([3, 25], CaptureComputeDocuments.UsableMarkers(WallMarkerLayout.Legacy(125), photos[0].MarkersJson).Select(m => m.Id));
        Assert.Empty(CaptureComputeDocuments.UsableMarkers(WallMarkerLayout.Legacy(125), photos[1].MarkersJson));
        Assert.Equal([17], CaptureComputeDocuments.IgnoredMarkers(photos[0].MarkersJson).Select(m => m.Id));
        Assert.Equal(2, photos.Count);
    }

    [Fact]
    public void TheSolverNotes_ListEveryIgnoredDetectionWithItsReason()
    {
        var photos = new[] { Photo(1, Real(3), Hold(17, CapturePlanLayoutCheck.Reason, "mirrored against markers 13 and 15")), Photo(46, Hold(17, CaptureMarkerDetection.NoQuietZoneReason)) };

        var json = CaptureIgnoredDetections.AddToModel(GlyphGeometryJson.Build(), photos);

        var rejected = WallGeometryDocument.Parse(json).Quality!.RejectedObservations!;
        Assert.Equal([("p01", 17, "plan-layout"), ("p46", 17, "no-quiet-zone")], rejected.Select(r => (r.Photo, r.Id, r.Reason!)));
        Assert.Equal(
            "Ignored marker 17 in IMG_1: doesn't fit the plan layout (mirrored against markers 13 and 15) — probably a false detection.",
            IgnoredDetectionFindings.Describe(rejected[0], "IMG_1"));
        Assert.Equal(
            "Ignored marker 17 in IMG_46: no white border around it — probably a hold or a shadow read as a marker.",
            IgnoredDetectionFindings.Describe(rejected[1], "IMG_46"));
    }

    [Fact]
    public void WithoutIgnoredDetections_TheModelIsUntouched()
    {
        var model = GlyphGeometryJson.Build();

        Assert.Same(model, CaptureIgnoredDetections.AddToModel(model, [Photo(1, Real(3))]));
    }

    [Fact]
    public void ADroppedWorstOffender_IsExplained()
    {
        var rejected = new WallGeometryRejectedObservation
        {
            Photo = "p78", Id = 17, Reason = WallGeometryRejectedObservation.ImplausibleModel, Detail = "a camera 881 km away",
        };

        Assert.Equal(
            "Ignored marker 17 in IMG_78: the model came out implausible with it (a camera 881 km away), so it was left out and the model solved again.",
            IgnoredDetectionFindings.Describe(rejected, "IMG_78"));
    }

    private static CaptureMarker Real(int id) => new(id, [[0, 0], [100, 0], [100, 100], [0, 100]], false, 100);

    private static CaptureMarker Hold(int id, string reason, string? detail = null) => Real(id) with { Ignored = reason, IgnoredDetail = detail };

    private static WallCapturePhoto Photo(int index, params CaptureMarker[] markers) => new()
    {
        Index = index,
        StoredPath = $"p{index}.jpg",
        ContentHash = $"h{index}",
        Width = 4032,
        Height = 3024,
        MarkersJson = JsonSerializer.Serialize(markers.ToList()),
    };
}
