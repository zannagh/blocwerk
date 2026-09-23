// <copyright file="IgnoredDetectionFindingsTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Nodes;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Tests.MarkerPlanning;

/// <summary>
/// Detections the solver removed as probable false detections (<c>quality.rejectedObservations</c>) show up
/// next to the placement check, under the photo name the admin uploaded.
/// </summary>
public class IgnoredDetectionFindingsTests
{
    private static readonly string AtticJson = File.ReadAllText(WallMarkerLayoutTests.Fixture("attic-wall-geometry.json"));

    private static readonly WallMarkerLayout Layout = WallMarkerLayout.FromPlan(AtticMarkerPlan.Plan);

    [Fact]
    public void RejectedObservations_AreReadFromTheSolverDocument()
    {
        var doc = WithRejected(
            """[{"photo":"p16","id":12,"views":15,"residualPx":10.3,"thresholdPx":8.1,"otherViewsResidualPx":11.2,"reason":"inconsistent-with-other-views","markerDropped":false}]""");

        var rejected = Assert.Single(doc.Quality!.RejectedObservations!);

        Assert.Equal(("p16", 12, 15, false), (rejected.Photo, rejected.Id, rejected.Views, rejected.MarkerDropped));
        Assert.Equal(WallGeometryRejectedObservation.InconsistentWithOtherViews, rejected.Reason);
        Assert.Equal(11.2, rejected.OtherViewsResidualPx);
    }

    [Fact]
    public void AnIgnoredDetection_IsNamedWithTheUploadedPhotoName()
    {
        var doc = WithRejected(
            """[{"photo":"p16","id":12,"views":15,"residualPx":10.3,"reason":"inconsistent-with-other-views","markerDropped":false}]""");
        var detected = doc.Markers.Select(m => m.Id).ToHashSet();

        var check = MarkerPlacementChecker.Check(Layout, doc, detected, name => name == "p16" ? "IMG_2803" : name);

        var finding = Assert.Single(check.Findings);
        Assert.Equal((MarkerPlacementIssue.IgnoredDetection, (int?)12), (finding.Kind, finding.MarkerId));
        Assert.Equal(
            "Ignored marker 12 in IMG_2803: doesn't match where the other photos place it — probably a false detection.",
            finding.Message);
    }

    [Fact]
    public void AMarkerDroppedAsASingleViewFalseDetection_IsExplained_NotReportedAsUnsolved()
    {
        var doc = WithRejected(
            """[{"photo":"p16","id":15,"views":1,"residualPx":10.3,"reason":"single-view-misfit","markerDropped":true}]""");
        doc = doc with { Markers = doc.Markers.Where(m => m.Id != 15).ToList() };
        var detected = new HashSet<int>(doc.Markers.Select(m => m.Id)) { 15 };

        var check = MarkerPlacementChecker.Check(Layout, doc, detected);

        var finding = Assert.Single(check.Findings);
        Assert.Equal(MarkerPlacementIssue.IgnoredDetection, finding.Kind);
        Assert.StartsWith("Ignored marker 15 in p16: no other photo shows it", finding.Message);
        Assert.Contains("probably a false detection", finding.Message);
    }

    [Fact]
    public void AnOlderSolverWithoutTheField_AddsNothing()
    {
        var doc = WallGeometryDocument.Parse(AtticJson);

        Assert.Null(doc.Quality!.RejectedObservations);
        Assert.Empty(IgnoredDetectionFindings.From(Layout, doc));
    }

    private static WallGeometryDocument WithRejected(string rejectedJson)
    {
        var node = JsonNode.Parse(AtticJson)!;
        node["quality"]!["rejectedObservations"] = JsonNode.Parse(rejectedJson);
        return WallGeometryDocument.Parse(node.ToJsonString());
    }
}
