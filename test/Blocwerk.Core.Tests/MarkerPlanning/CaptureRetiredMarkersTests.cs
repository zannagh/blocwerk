// <copyright file="CaptureRetiredMarkersTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// A retired id in capture photos never reaches the solver: not when freshly detected, not from photos stored before
/// it was retired (a re-solve of an old capture), not as an unplanned id, with or without a plan; it is listed in the
/// solver notes instead.
/// </summary>
public class CaptureRetiredMarkersTests
{
    private static readonly CaptureDeclarations Declarations = new([new CaptureSegmentDeclaration(0, "main wall", 45, false)], []);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The Attic's plan with marker 14 printed as 17, as plans made before the retirement may have it.</summary>
    private static readonly WallMarkerLayout PlanWith17 = WallMarkerLayout.FromPlan(AtticMarkerPlan.Plan with
    {
        Markers = AtticMarkerPlan.Plan.Markers.Select(m => m.Id == 14 ? m with { Id = 17 } : m).ToList(),
    });

    [Fact]
    public async Task AFreshDetectionOf17_IsStoredIgnored_TheOthersAreKept()
    {
        var detector = new FakeMarkerDetectionService(3, 17, 20);

        var markers = await CaptureMarkerDetection.DetectAsync(detector, [1], WallMarkerLayout.Legacy(125), null, CancellationToken.None);

        Assert.Equal([3, 20], markers.Where(m => m.Ignored is null).Select(m => m.Id));
        var retired = Assert.Single(markers, m => m.Id == 17);
        Assert.Equal(WallGeometryRejectedObservation.RetiredId, retired.Ignored);
        Assert.Contains("retired", retired.IgnoredDetail);
    }

    [Fact]
    public void AStored17FromBeforeTheRetirement_IsReadAsIgnored()
    {
        var photo = Photos((1, [3, 17])).Single();

        var markers = CaptureComputeDocuments.ParseMarkers(photo.MarkersJson);

        Assert.Equal(WallGeometryRejectedObservation.RetiredId, markers.Single(m => m.Id == 17).Ignored);
        Assert.Null(markers.Single(m => m.Id == 3).Ignored);
        Assert.Equal([3], CaptureComputeDocuments.UsableMarkers(PlanWith17, photo.MarkersJson).Select(m => m.Id));
    }

    [Fact]
    public void APlannedSheetOf17_SeenInEveryPhoto_NeverGoesToTheSolver()
    {
        Assert.Contains(17, PlanWith17.AllowedIds);
        var photos = Photos((1, [0, 1, 17]), (2, [0, 17]), (3, [1, 17]), (4, [17]));

        var request = JsonNode.Parse(CaptureComputeDocuments.BuildSolveRequest(PlanWith17, 125, Declarations, photos))!;

        var sent = request["photos"]!.AsArray().SelectMany(p => p!["markers"]!.AsArray().Select(m => (int)m!["id"]!)).ToList();
        Assert.DoesNotContain(17, sent);
        Assert.Equal(3, request["photos"]!.AsArray().Count);
    }

    [Fact]
    public void AnUnplanned17_InManyPhotos_IsNotSentAsAnUnplannedMarker()
    {
        var attic = WallMarkerLayout.FromPlan(AtticMarkerPlan.Plan);
        Assert.DoesNotContain(17, attic.AllowedIds);
        var photos = Photos((1, [0, 17, 49]), (2, [0, 17, 49]), (3, [1, 17, 49]));

        var request = JsonNode.Parse(CaptureComputeDocuments.BuildSolveRequest(attic, 125, Declarations, photos))!;

        Assert.Equal("[49]", request["unplannedMarkerIds"]!.ToJsonString());
        Assert.Empty(CaptureUnplannedMarkers.TooFewPhotos(attic, photos).Where(u => u.Marker.Id == 17));
    }

    [Fact]
    public void WithoutAPlan_17IsLeftOutToo()
    {
        var photos = Photos((1, [3, 17]), (2, [3, 17]));

        var request = JsonNode.Parse(CaptureComputeDocuments.BuildSolveRequest(125, Declarations, photos))!;

        var sent = request["photos"]!.AsArray().SelectMany(p => p!["markers"]!.AsArray().Select(m => (int)m!["id"]!)).ToList();
        Assert.Equal([3, 3], sent);
    }

    [Fact]
    public void TheLeftOutDetections_AreListedInTheSolverNotes()
    {
        var photos = Photos((1, [3, 17]), (2, [3]));

        var json = CaptureIgnoredDetections.AddToModel("""{"quality":{}}""", photos, PlanWith17);

        var record = Assert.Single(JsonNode.Parse(json)!["quality"]!["rejectedObservations"]!.AsArray())!;
        Assert.Equal((17, "p01", WallGeometryRejectedObservation.RetiredId), ((int)record["id"]!, (string)record["photo"]!, (string?)record["reason"]));
        var described = IgnoredDetectionFindings.Describe(record.Deserialize<WallGeometryRejectedObservation>(Json)!, "IMG_1.HEIC");
        Assert.StartsWith("Ignored marker 17 in IMG_1.HEIC: id 17 is retired", described);
    }

    private static List<WallCapturePhoto> Photos(params (int Index, int[] Ids)[] specs) => specs
        .Select(s => new WallCapturePhoto
        {
            Index = s.Index,
            StoredPath = $"p{s.Index}.jpg",
            ContentHash = $"h{s.Index}",
            Width = 4032,
            Height = 3024,
            Focal35mm = 24,
            CameraGroup = "cam",
            MarkersJson = JsonSerializer.Serialize(s.Ids.Select(id =>
                new CaptureMarker(id, [[1.5, 2], [3, 2], [3, 4], [1, 4]], false, 50)).ToList()),
        })
        .ToList();
}
