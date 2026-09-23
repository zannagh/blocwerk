// <copyright file="PlanSolveRequestTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// The solve request: byte for byte the old one without a plan; with a plan it says where every marker
/// sits and how big it is, and sends only planned markers.
/// </summary>
public class PlanSolveRequestTests
{
    private const string LegacyGolden =
        """{"markerSizeMm":125,"dictionary":"DICT_4X4_50","idScheme":"segment*6\u002Brole","segments":[{"index":0,"name":"main wall","declaredAngleDeg":45,"verticalReference":false},{"index":1,"name":"kickboard","declaredAngleDeg":0,"verticalReference":true}],"levelPairs":[[14,15]],"photos":[{"name":"p01","width":4032,"height":3024,"focal35mm":14,"cameraGroup":"cam","markers":[{"id":3,"corners":[[1.5,2],[3,2],[3,4],[1,4]],"refined":true,"synthetic":[false,false,false,false]},{"id":25,"corners":[[1.5,2],[3,2],[3,4],[1,4]],"refined":true,"synthetic":[false,false,false,false]}]},{"name":"p03","width":4032,"height":3024,"focal35mm":14,"cameraGroup":"cam","markers":[{"id":11,"corners":[[1.5,2],[3,2],[3,4],[1,4]],"refined":true,"synthetic":[true,true,true,true]}]}],"options":{"validate":false}}""";

    private static readonly CaptureDeclarations Declarations = new(
        [
            new CaptureSegmentDeclaration(0, "main wall", 45, false),
            new CaptureSegmentDeclaration(1, "kickboard", 0, true),
            new CaptureSegmentDeclaration(4, "Segment 4", null, false),
        ],
        [[14, 15]]);

    [Fact]
    public void WithoutAPlan_TheRequestIsExactlyTheOldOne()
    {
        var photos = Photos((1, [3, 25], false), (2, [], false), (3, [11], true));

        Assert.Equal(LegacyGolden, CaptureComputeDocuments.BuildSolveRequest(125, Declarations, photos));
        Assert.Equal(LegacyGolden, CaptureComputeDocuments.BuildSolveRequest(WallMarkerLayout.Legacy(125), 125, Declarations, photos));
    }

    [Fact]
    public void AtticPlan_SaysWhereEveryMarkerSits_AndDropsUnplannedOnes()
    {
        var layout = WallMarkerLayout.FromPlan(WallMarkerLayoutTests.LoadPlan("attic-plan.json"));
        var photos = Photos((1, [3, 25], false), (3, [11], false));

        var request = JsonNode.Parse(CaptureComputeDocuments.BuildSolveRequest(layout, layout.DefaultSizeMm!.Value, Declarations, photos))!;

        Assert.Equal("plan", (string?)request["idScheme"]);
        Assert.Equal(125, (double)request["markerSizeMm"]!);
        var segments = request["markerSegments"]!.AsObject();
        Assert.Equal(21, segments.Count);
        Assert.Equal(0, (int)segments["24"]!);
        Assert.Equal(2, (int)segments["15"]!);
        Assert.Empty(request["markerSizeOverridesMm"]!.AsObject());

        // Photo 3 only saw id 11, which the plan doesn't list: a false positive, not sent.
        var sent = request["photos"]!.AsArray();
        Assert.Single(sent);
        Assert.Equal([3, 25], sent[0]!["markers"]!.AsArray().Select(m => (int)m!["id"]!));
    }

    [Fact]
    public void SuggestedPlan_SendsEveryOtherSizeAsAnOverride()
    {
        var plan = WallMarkerLayoutTests.LoadPlan("attic-suggested.json");
        var layout = WallMarkerLayout.FromPlan(plan);
        var photos = Photos((1, plan.Markers.Take(3).Select(m => m.Id).ToArray(), false));

        var request = JsonNode.Parse(CaptureComputeDocuments.BuildSolveRequest(layout, layout.DefaultSizeMm!.Value, Declarations, photos))!;

        var overrides = request["markerSizeOverridesMm"]!.AsObject();
        var odd = plan.Markers.Where(m => m.SizeMm != layout.DefaultSizeMm).ToList();
        Assert.NotEmpty(odd);
        Assert.Equal(odd.Count, overrides.Count);
        Assert.All(odd, m => Assert.Equal(m.SizeMm, (double)overrides[m.Id.ToString()]!));
    }

    private static List<WallCapturePhoto> Photos(params (int Index, int[] Ids, bool Synthetic)[] specs) => specs
        .Select(s => new WallCapturePhoto
        {
            Index = s.Index,
            StoredPath = $"p{s.Index}.jpg",
            ContentHash = $"h{s.Index}",
            Width = 4032,
            Height = 3024,
            Focal35mm = 14,
            CameraGroup = "cam",
            MarkersJson = JsonSerializer.Serialize(s.Ids.Select(id =>
                new CaptureMarker(id, [[1.5, 2], [3, 2], [3, 4], [1, 4]], s.Synthetic, 50)).ToList()),
        })
        .ToList();
}
