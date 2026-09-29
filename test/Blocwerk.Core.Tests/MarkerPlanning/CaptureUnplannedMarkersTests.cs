// <copyright file="CaptureUnplannedMarkersTests.cs" company="Blocwerk">
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
/// Markers stuck on after the plan was printed: decoded with a plan, sent to the solver (without a segment) once
/// enough photos agree, listed in the solver notes when too few do; walls without a plan are unchanged.
/// </summary>
public class CaptureUnplannedMarkersTests
{
    private static readonly WallMarkerLayout Attic = WallMarkerLayout.FromPlan(WallMarkerLayoutTests.LoadPlan("attic-plan.json"));

    private static readonly CaptureDeclarations Declarations = new([new CaptureSegmentDeclaration(0, "main wall", 45, false)], []);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public void WithAPlan_EveryDictionaryIdIsDecoded_WithoutOne_OnlyTheLegacyIds()
    {
        Assert.True(CaptureUnplannedMarkers.DetectionOptions(Attic).AllowedIds.SetEquals(Enumerable.Range(0, 50)));
        Assert.Equal(Attic.DetectionOptions.MinQuietZoneContrast, CaptureUnplannedMarkers.DetectionOptions(Attic).MinQuietZoneContrast);
        var legacy = WallMarkerLayout.Legacy(125);
        Assert.Same(legacy.DetectionOptions.AllowedIds, CaptureUnplannedMarkers.DetectionOptions(legacy).AllowedIds);
    }

    [Fact]
    public void AnUnplannedIdInThreePhotos_GoesToTheSolver_OneInTwoDoesNot()
    {
        Assert.DoesNotContain(49, Attic.AllowedIds);
        Assert.DoesNotContain(11, Attic.AllowedIds);
        var photos = Photos((1, [0, 1, 49, 11]), (2, [0, 49, 11]), (3, [1, 49]), (4, [12]), (5, [46]));

        var request = JsonNode.Parse(CaptureComputeDocuments.BuildSolveRequest(Attic, 125, Declarations, photos))!;

        Assert.Equal("[49]", request["unplannedMarkerIds"]!.ToJsonString());
        Assert.False(request["markerSegments"]!.AsObject().ContainsKey("49"));
        var sent = request["photos"]!.AsArray().ToDictionary(p => (string)p!["name"]!, p => p!["markers"]!.AsArray().Select(m => (int)m!["id"]!).ToList());
        Assert.Equal(["p01", "p02", "p03", "p04"], sent.Keys);
        Assert.Equal([0, 1, 49], sent["p01"]);
        Assert.Equal([1, 49], sent["p03"]);
    }

    [Fact]
    public void AnUnplannedIdTooFewPhotosShow_IsListedInTheSolverNotes()
    {
        var photos = Photos((1, [0, 11]), (2, [0, 11]), (3, [0, 49]), (4, [49]), (5, [49]));

        var json = CaptureIgnoredDetections.AddToModel("""{"quality":{}}""", photos, Attic);

        var rejected = JsonNode.Parse(json)!["quality"]!["rejectedObservations"]!.AsArray();
        var record = Assert.Single(rejected)!;
        Assert.Equal((11, "p01", true), ((int)record["id"]!, (string)record["photo"]!, (bool)record["markerDropped"]!));
        Assert.Equal(WallGeometryRejectedObservation.UnplannedFewPhotos, (string?)record["reason"]);
        Assert.Contains("2 photos", (string)record["detail"]!);
        var described = IgnoredDetectionFindings.Describe(record.Deserialize<WallGeometryRejectedObservation>(Json)!, "IMG_1.HEIC");
        Assert.StartsWith("Ignored marker 11 in IMG_1.HEIC: not in the marker plan", described);
    }

    [Fact]
    public void WithoutAPlan_NoIdIsUnplanned()
    {
        var photos = Photos((1, [3, 40]), (2, [3, 40]), (3, [3, 40]));

        var request = JsonNode.Parse(CaptureComputeDocuments.BuildSolveRequest(125, Declarations, photos))!;

        Assert.Null(request["unplannedMarkerIds"]);
        Assert.Empty(CaptureUnplannedMarkers.Consistent(WallMarkerLayout.Legacy(125), photos));
    }

    [Fact]
    public void ThePhotosCameraGroup_IsSplitByTheFocalLength_SoA12xCropNeverSharesThe1xIntrinsics()
    {
        var photos = Photos((1, [0, 1]), (2, [0, 1]), (3, [0, 1]));
        photos[1].Focal35mm = 28;
        photos[2].Focal35mm = null;

        var groups = JsonNode.Parse(CaptureComputeDocuments.BuildSolveRequest(Attic, 125, Declarations, photos))!["photos"]!.AsArray()
            .Select(p => (string?)p!["cameraGroup"]).ToList();

        Assert.Equal(["cam|f35=24", "cam|f35=28", "cam"], groups);
        Assert.Null(CaptureComputeDocuments.SolveCameraGroup(new WallCapturePhoto { StoredPath = "x", ContentHash = "h", Focal35mm = 24 }));
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
