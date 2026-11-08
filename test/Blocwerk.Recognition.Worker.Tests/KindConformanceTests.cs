// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Recognition.Worker.Jobs;

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>The markers, match-panels and detect-holds@2 documents against the runner protocol 1.2 fixtures.</summary>
public class KindConformanceTests
{
    private static readonly MarkerPoint[] Corners = [new(100.0, 100.0), new(180.0, 102.0), new(178.0, 181.0), new(99.0, 180.0)];

    [Fact]
    public void RequestFixturesParse()
    {
        var detect = ContractFixtures.Read<DetectRequest>("jobs/recognition/detect-request/valid/app-defaults-with-model.json");
        var markers = ContractFixtures.Read<MarkersRequest>("jobs/recognition/markers-request/valid/plan-printed.json").ToOptions();
        var match = ContractFixtures.Read<MatchRequest>("jobs/recognition/match-request/valid/with-marker-seed.json");

        Assert.Equal((1280, 256, "nearest"), (detect.Tiling.TileSize, detect.Tiling.Overlap, detect.Tiling.Sampling));
        Assert.StartsWith("9f2c1e4b", detect.Model.Sha256, StringComparison.Ordinal);
        Assert.Equal(new HashSet<int> { 0, 1, 2, 35 }, markers.AllowedIds);
        Assert.Equal((0.1, 20.0), (markers.MinQuietZoneContrast, markers.MinSidePx));
        var seed = match.Seed!.ToSeed();
        Assert.Equal((HoldOverlapSeedSource.SharedMarkers, 3, true), (seed.Source, seed.MarkerCount, double.IsNaN(seed.FitRmsPx)));
        Assert.Equal(new HoldOverlapPointPair(0.9, 0.5, 0.1, 0.5), seed.MeasuredPairs.Single());
        Assert.Equal(3, match.LeftHolds[1].ToHold().Shape!.Count);
        Assert.Equal(new MatcherColor(120, 130, 140), match.LeftHolds[0].Color);
    }

    [Fact]
    public void MarkerResultsMatchTheFixture()
    {
        var result = new MarkerDetectionResult
        {
            ImageWidth = 4000,
            ImageHeight = 3000,
            Markers = [new DetectedMarker { Id = 7, CornersPx = Corners, CornersNormalized = Corners.Select(c => new MarkerPoint(c.X / 4000, c.Y / 3000)).ToList(), SidePx = 79.6, EdgeRatio = 1.02 }],
            Rejected = [new RejectedMarkerCandidate(40, Corners, 12.0, 1.1, MarkerRejectionReason.IdNotAllowed, "id 40 is not on the wall")],
            UndecodedCandidateCount = 3,
        };

        Assert.Equal(ContractFixtures.CanonicalFixture("jobs/recognition/markers-result/valid/one-marker.json"), ContractFixtures.Canonical(MarkersHandler.ToResult(result)));
    }

    [Fact]
    public void MatchResultsMatchTheFixture()
    {
        var match = new HoldOverlapResult(
            [new HoldOverlapProposal(1, 1, 0.82, false, 3.4, null)],
            [2],
            [],
            [(0.1, 0.5), null],
            [null, [(0.09, 0.29), (0.11, 0.29), (0.1, 0.31)]]);

        Assert.Equal(ContractFixtures.CanonicalFixture("jobs/recognition/match-result/valid/one-pair.json"), ContractFixtures.Canonical(MatchPanelsHandler.ToResult(match)));
    }
}
