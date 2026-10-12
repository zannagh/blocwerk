// <copyright file="HoldColorPromoteTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.LookOver;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>
/// The promote of a panel update (the browser's Apply calls it directly): its new holds get the colour the new photo
/// shows, carried holds keep what they had, linked holds get one colour, and a failing proposal step never fails it.
/// </summary>
public class HoldColorPromoteTests
{
    [Fact]
    public async Task NewHolds_GetAProposal_AndCarriedHoldsKeepWhatTheyHad()
    {
        using var s = await HoldColorScenario.CreateAsync(oldHolds: 3);
        var old = await OldHoldIdsAsync(s);
        await s.SetAsync(old[0], h => h.Color = "yellow");
        await s.SetAsync(old[2], h => (h.Color, h.ProposedColor) = ("pink", "pink"));
        var panel = await s.AddPanelAsync(0, generation: 1, staged: true);
        var twinOfSet = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot, generation: 1);
        var twinOfUnset = await s.AddHoldAsync(panel, HoldColorScenario.RedSpot, generation: 1);
        var twinOfProposed = await s.AddHoldAsync(panel, HoldColorScenario.GreenSpot, generation: 1);
        var fresh = await s.AddHoldAsync(panel, HoldColorScenario.YellowSpot, generation: 1);
        var freshOnTheWall = await s.AddHoldAsync(panel, HoldColorScenario.WallSpot, generation: 1);
        var confirmation = new BigUpdateConfirmation(
            [new(old[0], CarryKind.Carried, twinOfSet), new(old[1], CarryKind.Carried, twinOfUnset), new(old[2], CarryKind.Carried, twinOfProposed)],
            [fresh, freshOnTheWall],
            [],
            []);

        await s.BigUpdate().PromoteAsync(s.WallId, confirmation);

        var proposed = await s.GetAsync(fresh);
        Assert.Equal((1, "yellow", true), (proposed.Generation, proposed.Color, proposed.HasProposedColor));
        Assert.Null((await s.GetAsync(freshOnTheWall)).Color);

        // The carried holds sit on other colours in the photo: a colour somebody set stays, an unset one stays unset,
        // and a proposal nobody confirmed is still a proposal.
        var set = await s.GetAsync(twinOfSet);
        Assert.Equal(("yellow", false), (set.Color, set.HasProposedColor));
        Assert.Null((await s.GetAsync(twinOfUnset)).Color);
        var carriedProposal = await s.GetAsync(twinOfProposed);
        Assert.Equal(("pink", true), (carriedProposal.Color, carriedProposal.HasProposedColor));
    }

    [Fact]
    public async Task ANewHoldOnTwoPanels_GetsOneColour_FromTheViewThatShowsIt()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var centre = await s.AddPanelAsync(0, generation: 1, staged: true);
        var right = await s.AddPanelAsync(1, generation: 1, staged: true, photo: HoldColorScenario.Photo(greenSpot: HoldColorTestPhoto.Wood));
        var onCentre = await s.AddHoldAsync(centre, HoldColorScenario.GreenSpot, generation: 1);
        var onRight = await s.AddHoldAsync(right, HoldColorScenario.GreenSpot, generation: 1);
        var confirmation = new BigUpdateConfirmation(
            [], [onCentre], [], [new NeighbourLinkSet(right, [new ConfirmedLink(onCentre, onRight, false)], [])]);

        await s.BigUpdate().PromoteAsync(s.WallId, confirmation);

        await using var db = s.Harness.CreateContext();
        Assert.Single(await db.HoldLinks.Where(l => l.HoldAId == onCentre && l.HoldBId == onRight).ToListAsync());
        var (a, b) = (await s.GetAsync(onCentre), await s.GetAsync(onRight));
        Assert.Equal(("green", "green"), (a.Color, b.Color));
        Assert.True(a.HasProposedColor && b.HasProposedColor);
    }

    [Fact]
    public async Task AFailingProposalStep_DoesNotFailThePromote_AndLeavesNothingHalfWritten()
    {
        using var s = await HoldColorScenario.CreateAsync();
        var panel = await s.AddPanelAsync(0, generation: 1, staged: true);
        var fresh = await s.AddHoldAsync(panel, HoldColorScenario.BlueSpot, generation: 1);
        var proposer = Substitute.For<IHoldColorProposer>();
        proposer.PlanAsync(Arg.Any<BlocwerkDbContext>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(new HoldColorPlan([]));
        proposer.ApplyAsync(Arg.Any<BlocwerkDbContext>(), Arg.Any<Guid>(), Arg.Any<HoldColorPlan>(), Arg.Any<CancellationToken>())
            .Returns<Task<HoldColorProposalReport>>(async call =>
            {
                (await call.Arg<BlocwerkDbContext>().Holds.FirstAsync(h => h.Id == fresh)).Color = "black";
                throw new InvalidOperationException("the photo store is down");
            });

        await s.BigUpdate(proposer).PromoteAsync(s.WallId, new BigUpdateConfirmation([], [fresh], [], []));

        await using var db = s.Harness.CreateContext();
        Assert.Equal(1, (await db.Walls.FirstAsync(w => w.Id == s.WallId)).CurrentGeneration);
        var hold = await s.GetAsync(fresh);
        Assert.Equal((1, null), (hold.Generation, hold.Color));
        await proposer.Received(1).PlanAsync(
            Arg.Any<BlocwerkDbContext>(), s.WallId, Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids!.Single() == fresh), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AFailingProposalStep_StillLeavesTheLookOverFindings()
    {
        using var h = new WallTestHarness();
        var proposer = Substitute.For<IHoldColorProposer>();
        proposer.PlanAsync(Arg.Any<BlocwerkDbContext>(), Arg.Any<Guid>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns<Task<HoldColorPlan>>(_ => throw new InvalidOperationException("the photo store is down"));

        // Another hold sits on the old hold's spot now: the detection becomes a new hold (no colour) and a finding.
        var w = await HoldLookFixture.SeedAsync(h, targetDetected: true);
        w = w with
        {
            Service = new WallBigUpdateService(
                h.DbContextFactory, h.CurrentUser, h.HoldDetection, new SameFrameHoldMatcher(), NullLogger<WallBigUpdateService>.Instance,
                presenceProbe: w.Probe, colorProposer: proposer),
        };
        w.Probe.Set(HoldLookFixture.TargetX, HoldLookFixture.TargetY, 0.1);
        var session = await w.Service.ResumeAsync(w.WallId);
        await HoldLookFixture.RecordAsync(w, session);
        await LookOverFixture.PromoteLikeApplyAsync(w, session);

        await proposer.Received(1).PlanAsync(
            Arg.Any<BlocwerkDbContext>(), w.WallId, Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids!.Contains(w.DetectionId!.Value)), Arg.Any<CancellationToken>());
        var finding = await LookOverFixture.FindingAsync(h, w.WallId);
        Assert.Equal((LookOverFindingKind.ReplacedByOther, LookOverFindingStatus.Open), (finding.Kind, finding.Status));
        var wall = await LookOverFixture.WallAsync(h, w.WallId);
        Assert.Equal(3, wall.CurrentGeneration);
        Assert.NotNull(wall.LookOverPendingSince);
        Assert.Null((await LookOverFixture.HoldAsync(h, w.DetectionId))!.Color);
    }

    private static async Task<List<Guid>> OldHoldIdsAsync(HoldColorScenario s)
    {
        await using var db = s.Harness.CreateContext();
        return await db.Holds.Where(h => h.WallId == s.WallId && h.Generation == 0).OrderBy(h => h.X).Select(h => h.Id).ToListAsync();
    }
}
