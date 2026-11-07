// <copyright file="BigUpdateStagingGateTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Staging recognises one panel at a time: each panel's detection and enrichment run inside one gate slot of their
/// own, so no two panels' decoded photos and model buffers are alive together.
/// </summary>
public class BigUpdateStagingGateTests
{
    [Fact]
    public async Task Stage_DetectsAndEnrichesEachPanelInsideItsOwnSlot()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var gate = new RecordingRecognitionGate();
        var seen = new List<(string Step, int Run)>();
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ =>
            {
                seen.Add(("detect", gate.Inside));
                return Task.FromResult(new List<DetectedHold> { new(0.5, 0.5, 0.02, null, 0.9) });
            });
        var enrichment = Substitute.For<IHoldEnrichmentService>();
        enrichment.EnrichAsync(default!, default!, default).ReturnsForAnyArgs(_ =>
        {
            seen.Add(("enrich", gate.Inside));
            return Task.FromResult(HoldEnrichmentSummary.None);
        });
        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance, holdEnrichment: enrichment, recognitionGate: gate);

        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentreAndNeighbour());

        Assert.Equal(["stage-panel", "stage-panel"], gate.Runs);
        Assert.Equal([("detect", 1), ("enrich", 1), ("detect", 2), ("enrich", 2)], seen);
    }
}
