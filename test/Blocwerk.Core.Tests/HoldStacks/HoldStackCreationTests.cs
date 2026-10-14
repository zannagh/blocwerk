// <copyright file="HoldStackCreationTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.HoldColors;
using Blocwerk.Core.Tests.LookOver;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.HoldStacks;

/// <summary>Wherever a hold is created, one that lies on a clearly larger hold of its photo is stacked on it.</summary>
public class HoldStackCreationTests
{
    private static readonly (double X, double Y) Spot = (0.5, 0.5);

    [Fact]
    public async Task AHoldAddedByHand_SitsOnTheVolumeUnderIt_AndAVirtualOneNever()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var volume = await s.AddHoldAsync(panel, Spot, configure: h => (h.Radius, h.ShapePoints) = (0.12, Entities.ShapePoint.DefaultOctagon(0.12)));

        var added = await s.Harness.WallService.AddHoldAsync(s.WallId, 0.52, 0.5, 0.02, null, wallPanelId: panel);
        var virt = await s.Harness.WallService.AddHoldAsync(s.WallId, 0.48, 0.5, 0.02, null, isVirtual: true, wallPanelId: panel);
        var elsewhere = await s.Harness.WallService.AddHoldAsync(s.WallId, 0.9, 0.9, 0.02, null, wallPanelId: panel);

        Assert.Equal(volume, (await s.GetAsync(added.Id)).StackedOnHoldId);
        Assert.Null((await s.GetAsync(virt.Id)).StackedOnHoldId);
        Assert.Null((await s.GetAsync(elsewhere.Id)).StackedOnHoldId);
    }

    [Fact]
    public async Task AVolumeAddedByHandWithItsOutline_TakesTheHoldsThatLieOnIt()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var small = await s.AddHoldAsync(panel, Spot, configure: h => h.Radius = 0.02);

        var volume = await Panels(s).AddPanelHoldAsync(s.WallId, panel, 0.5, 0.5, 0.12, shapePoints: Entities.ShapePoint.DefaultOctagon(0.12));

        Assert.Equal(volume, (await s.GetAsync(small)).StackedOnHoldId);
    }

    [Fact]
    public async Task ReDetect_StacksTheFreshDetectionsAmongThemselves()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        Detects(s, (0.5, 0.5, 0.12), (0.53, 0.5, 0.02), (0.47, 0.52, 0.02), (0.9, 0.2, 0.02));

        var redetect = new Blocwerk.Core.Redetect.PanelRedetectService(
            s.Harness.DbContextFactory, s.Harness.CurrentUser, s.Harness.HoldDetection, new Blocwerk.Core.Redetect.HoldSamenessOptions(),
            NullLogger<Blocwerk.Core.Redetect.PanelRedetectService>.Instance, holdEnrichment: OutlinesLargeHolds(from: 0));
        await redetect.ApplyAsync(await redetect.PlanAsync(s.WallId, panel));

        await using var db = s.Harness.CreateContext();
        var holds = await db.Holds.Where(h => h.WallPanelId == panel).OrderByDescending(h => h.Radius).ThenBy(h => h.Y).ToListAsync();
        Assert.Equal([null, null, holds[0].Id, holds[0].Id], holds.Select(h => h.StackedOnHoldId));
    }

    [Fact]
    public async Task AStagedPanelUpdate_StacksItsDetections_AndAHoldAddedInTheReview()
    {
        using var s = await HoldColorScenario.CreateAsync();
        Detects(s, (0.5, 0.5, 0.12), (0.53, 0.5, 0.02));
        var update = new WallBigUpdateService(
            s.Harness.DbContextFactory, s.Harness.CurrentUser, s.Harness.HoldDetection, Substitute.For<IHoldOverlapMatcher>(),
            NullLogger<WallBigUpdateService>.Instance, holdEnrichment: OutlinesLargeHolds());

        await update.StageAsync(s.WallId, WallUpdateSessionFixture.CentrePhoto());

        var panel = await WallUpdateSessionFixture.PanelIdAsync(s.Harness, 0, 0);
        var added = await Panels(s).AddStagedHoldAsync(s.WallId, panel, 0.46, 0.5, 0.02);
        await using var db = s.Harness.CreateContext();
        var holds = await db.Holds.Where(h => h.WallPanelId == panel).OrderByDescending(h => h.Radius).ToListAsync();
        Assert.Equal(3, holds.Count);
        Assert.All(holds.Skip(1), h => Assert.Equal(holds[0].Id, h.StackedOnHoldId));
        Assert.Contains(holds, h => h.Id == added);
    }

    [Fact]
    public async Task ADiscardedDetectionOnAVolume_IsNoDuplicateOfIt_AndAddedAsAHoldItSitsOnIt()
    {
        using var s = await DiscardedScenario.ApplyAsync((HoldColorScenario.YellowSpot, 0.97, NewHoldDiscardReason.UnchangedSinceOldPhoto));
        var volume = await s.Colors.AddHoldAsync(
            s.PanelId, HoldColorScenario.YellowSpot, generation: 1, h => (h.Radius, h.ShapePoints) = (0.15, Entities.ShapePoint.DefaultOctagon(0.15)));
        var service = s.LookOver();

        // The volume covers the detection completely, and still it is a hold the update may have missed, not a duplicate.
        var card = Assert.Single(await service.ListAsync(s.WallId));
        Assert.Equal(0, card.Overlap);
        await service.AnswerAsync(s.WallId, card.Id, LookOverAnswer.AddHold, []);

        var added = Assert.Single(await s.FindingsAsync()).HoldId!.Value;
        Assert.Equal(volume, (await s.Colors.GetAsync(added)).StackedOnHoldId);
    }

    private static WallPanelService Panels(HoldColorScenario s, IHoldEnrichmentService? enrichment = null) => new(
        s.Harness.DbContextFactory, s.Harness.CurrentUser, s.Harness.HoldDetection, Substitute.For<IHoldOverlapMatcher>(),
        NullLogger<WallPanelService>.Instance, holdEnrichment: enrichment);

    /// <summary>An enrichment that traces the large detections (an octagon) and leaves the small ones plain circles.</summary>
    private static IHoldEnrichmentService OutlinesLargeHolds(double from = 0.1)
    {
        var enrichment = Substitute.For<IHoldEnrichmentService>();
        enrichment.EnrichAsync(default!, default!, default).ReturnsForAnyArgs(call =>
        {
            foreach (var hold in call.Arg<HoldEnrichmentRequest>().Holds.Where(h => h.Radius >= from))
            {
                hold.ShapePoints = Entities.ShapePoint.DefaultOctagon(hold.Radius);
            }

            return Task.FromResult(HoldEnrichmentSummary.None);
        });
        return enrichment;
    }

    private static void Detects(HoldColorScenario s, params (double X, double Y, double Radius)[] holds)
    {
        s.Harness.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(holds.Select(h => new DetectedHold(h.X, h.Y, h.Radius, null, 0.9)).ToList()));
    }
}
