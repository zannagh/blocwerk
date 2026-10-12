// <copyright file="HoldColorRaceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Holds.Colors;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>
/// Reading the photos takes seconds, and the hold editor takes no wall lock: what changes in between is respected. A
/// colour set meanwhile is kept, the wall is not locked while the photos are read, and a wooden hold never gets a
/// plastic colour.
/// </summary>
public class HoldColorRaceTests
{
    [Fact]
    public async Task AColourSetBetweenTheEstimateAndTheWrite_IsKept_AlsoWhenTheContextStillTracksTheOldRow()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var edited = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot);
        var deleted = await s.AddHoldAsync(panel, HoldColorScenario.RedSpot);
        var untouched = await s.AddHoldAsync(panel, HoldColorScenario.GreenSpot);
        var proposer = new HoldColorProposer();
        await using var db = s.Harness.CreateContext();

        // As in the promote: the context has the hold tracked, without a colour.
        var tracked = await db.Holds.FirstAsync(h => h.Id == edited);
        var plan = await proposer.PlanAsync(db, s.WallId);
        await s.SetAsync(edited, h => h.Color = "purple");
        await using (var other = s.Harness.CreateContext())
        {
            other.Holds.Remove(await other.Holds.FirstAsync(h => h.Id == deleted));
            await other.SaveChangesAsync();
        }

        var report = await proposer.ApplyAsync(db, s.WallId, plan);
        await db.SaveChangesAsync();

        Assert.Equal((3, 1, 2), (report.Considered, report.Proposed, report.ChangedMeanwhile));
        Assert.Null(tracked.ProposedColor);
        var kept = await s.GetAsync(edited);
        Assert.Equal(("purple", false), (kept.Color, kept.HasProposedColor));
        Assert.Equal("green", (await s.GetAsync(untouched)).Color);
    }

    [Fact]
    public async Task WhileThePhotosAreRead_TheWallIsNotLocked_AndAColourSetThenIsKept()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0);
        var edited = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot);
        var other = await s.AddHoldAsync(panel, HoldColorScenario.RedSpot);
        var real = new HoldColorProposer();
        var proposer = Substitute.For<IHoldColorProposer>();
        proposer.PlanAsync(Arg.Any<BlocwerkDbContext>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var plan = await real.PlanAsync(call.Arg<BlocwerkDbContext>(), call.Arg<Guid>());

                // A panel update (or anything else that needs the wall's holds) can start now, and the editor saves.
                WallHoldWriteLock.TryAcquire(s.WallId, "the wall is locked while the photos are read").Dispose();
                await s.SetAsync(edited, h => h.Color = "purple");
                return plan;
            });
        proposer.ApplyAsync(Arg.Any<BlocwerkDbContext>(), Arg.Any<Guid>(), Arg.Any<HoldColorPlan>(), Arg.Any<CancellationToken>())
            .Returns(call => real.ApplyAsync(call.Arg<BlocwerkDbContext>(), call.Arg<Guid>(), call.Arg<HoldColorPlan>()));
        var service = new HoldColorProposalService(
            s.Harness.DbContextFactory, s.Harness.CurrentUser, proposer, NullLogger<HoldColorProposalService>.Instance);

        var report = await service.SuggestMissingAsync(s.WallId);

        Assert.Equal((1, 1), (report.Proposed, report.ChangedMeanwhile));
        Assert.Equal("purple", (await s.GetAsync(edited)).Color);
        Assert.Equal("red", (await s.GetAsync(other)).Color);
    }

    [Fact]
    public async Task AWoodenHold_NeverGetsAPlasticColour_NotFromItsPhotoAndNotFromALinkedHold()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var centre = await s.AddPanelAsync(0);
        var right = await s.AddPanelAsync(1);
        var wooden = await s.AddHoldAsync(centre, HoldColorScenario.BlueSpot, configure: h => h.Material = HoldMaterial.Wood);
        var plastic = await s.AddHoldAsync(centre, HoldColorScenario.GreenSpot, configure: h => h.Material = HoldMaterial.PU);
        var setByHand = await s.AddHoldAsync(centre, HoldColorScenario.RedSpot, configure: h => h.Color = "red");
        var woodenTwin = await s.AddHoldAsync(right, HoldColorScenario.RedSpot, configure: h => h.Material = HoldMaterial.Wood);
        await s.LinkAsync(setByHand, woodenTwin);

        var report = await s.Service().SuggestMissingAsync(s.WallId);

        Assert.Equal(2, report.UnsetByVerdict[HoldColorVerdict.NotForMaterial]);
        Assert.Equal((1, 0, 2), (report.Proposed, report.FromLinked, report.LeftUnset));
        Assert.Null((await s.GetAsync(wooden)).Color);
        Assert.Null((await s.GetAsync(woodenTwin)).Color);
        Assert.Equal("green", (await s.GetAsync(plastic)).Color);
    }
}
