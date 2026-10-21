// <copyright file="HoldProposalRetirementTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>Pending proposals stay tidy: a superseded model's unseen ones and same-spot twins are retired, review items follow.</summary>
public sealed class HoldProposalRetirementTests
{
    private static readonly Guid Model = Guid.NewGuid();
    private static readonly Guid OldModel = Guid.NewGuid();

    [Fact]
    public void ANewPass_KeepsTheStrongerOfTwoOnOneSpot_AndRetiresEveryPendingProposalOfASupersededModel()
    {
        var oldUnseen = Pending(500, 0, OldModel);
        var oldOnASpot = Pending(3, 0, OldModel);
        var strong = Pending(1000, 0, Model, views: 5);
        var weak = Pending(1010, 0, Model, views: 2);
        var fresh = Pending(0, 0, Model);

        var plan = HoldProposalRetirement.Plan([oldUnseen, oldOnASpot, strong, weak, fresh], Model, _ => false);

        Assert.Equal(3, plan.Count);
        Assert.Contains(plan, r => r.Retired == weak && r.Survivor == strong);
        Assert.Contains(plan, r => r.Retired == oldUnseen && r.Survivor is null);
        Assert.Contains(plan, r => r.Retired == oldOnASpot && r.Survivor is null);
    }

    [Fact]
    public void AnOldModelsProposal_OnTheSpotOfANewRow_IsRetiredWithThatRowAsSurvivor_WhenTheModelsShareAFrame()
    {
        var old = Pending(3, 0, OldModel);
        var fresh = Pending(0, 0, Model);

        var shared = HoldProposalRetirement.Plan([old, fresh], Model, m => m == OldModel);
        var separate = HoldProposalRetirement.Plan([old, fresh], Model, _ => false);

        Assert.Equal(fresh, Assert.Single(shared).Survivor);
        Assert.Null(Assert.Single(separate).Survivor);
        Assert.True(HoldProposalRetirement.SameFrame((WallGeometryFrameSource.Markers, 3), (WallGeometryFrameSource.Markers, 3)));
        Assert.False(HoldProposalRetirement.SameFrame((WallGeometryFrameSource.Markers, 3), (WallGeometryFrameSource.Markers, 4)));
        Assert.False(HoldProposalRetirement.SameFrame((WallGeometryFrameSource.Features, null), (WallGeometryFrameSource.Features, null)));
        Assert.False(HoldProposalRetirement.SameFrame((WallGeometryFrameSource.Markers, null), (WallGeometryFrameSource.Markers, null)));
    }

    [Fact]
    public async Task RePointing_NeverLeavesTwoOpenReviewItemsOnOneProposal()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var (twin, survivor) = (Pending(1010, 0, Model), Pending(1000, 0, Model, views: 5));
        await using var db = f.Harness.CreateContext();
        var run = new WallEnhancementRun { WallId = f.WallId, Generation = 0, CreatedByUserId = f.Harness.Owner.Id, Status = WallEnhancementStatus.Review };
        db.WallEnhancementRuns.Add(run);
        db.WallEnhancementItems.AddRange(
            new WallEnhancementItem { RunId = run.Id, Kind = WallEnhancementItemKind.NewHold, ReferenceId = twin.Id },
            new WallEnhancementItem { RunId = run.Id, Kind = WallEnhancementItemKind.NewHold, ReferenceId = survivor.Id });
        await db.SaveChangesAsync();

        await HoldProposalRetirement.ApplyAsync(db, [new ProposalRetirement(twin, survivor)], default);
        await db.SaveChangesAsync();

        var items = await db.WallEnhancementItems.AsNoTracking().ToListAsync();
        Assert.Single(items, i => i.Decision == WallEnhancementDecision.Pending && i.ReferenceId == survivor.Id);
        Assert.Contains("already in this review", items.Single(i => i.Decision == WallEnhancementDecision.Skipped).Note);
    }

    [Fact]
    public async Task RetiringProposals_RePointsTheEnhancementItemsAtTheSurvivor_OrClosesThem()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var (gone, twin, survivor) = (Pending(500, 0, OldModel), Pending(1010, 0, Model), Pending(1000, 0, Model, views: 5));
        await using var db = f.Harness.CreateContext();
        var run = new WallEnhancementRun { WallId = f.WallId, Generation = 0, CreatedByUserId = f.Harness.Owner.Id, Status = WallEnhancementStatus.Review };
        db.WallEnhancementRuns.Add(run);
        db.WallEnhancementItems.AddRange(
            new WallEnhancementItem { RunId = run.Id, Kind = WallEnhancementItemKind.NewHold, ReferenceId = gone.Id },
            new WallEnhancementItem { RunId = run.Id, Kind = WallEnhancementItemKind.NewHold, ReferenceId = twin.Id });
        await db.SaveChangesAsync();

        await HoldProposalRetirement.ApplyAsync(db, [new ProposalRetirement(gone, null), new ProposalRetirement(twin, survivor)], default);
        await db.SaveChangesAsync();

        Assert.Equal((HoldProposalStatus.Retired, HoldProposalStatus.Retired), (gone.Status, twin.Status));
        var items = await db.WallEnhancementItems.AsNoTracking().ToListAsync();
        Assert.Equal(WallEnhancementDecision.Skipped, items.Single(i => i.ReferenceId == gone.Id).Decision);
        Assert.Equal(WallEnhancementDecision.Pending, items.Single(i => i.ReferenceId == survivor.Id).Decision);
    }

    [Fact]
    public void TheEta_ForgetsAResumedSearchsRushThroughItsCheckpoint()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = now;
        var tracker = new WallEnhancementStepTracker(Guid.NewGuid(), null, () => clock) { Window = 5 };
        for (var done = 0; done <= 150; done += 10)
        {
            tracker.Report(new WallEnhancementProgress(done, 425));
        }

        for (var done = 151; done <= 160; done++)
        {
            clock = clock.AddSeconds(7);
            tracker.Report(new WallEnhancementProgress(done, 425));
        }

        Assert.Equal(clock.AddSeconds(7 * 265), tracker.ExpectedEnd);
    }

    private static HoldProposal Pending(double x, double y, Guid model, int views = 3) => new()
    {
        WallId = Guid.NewGuid(), GeometryModelId = model, FacetId = "f", BestPhoto = "p", X = x, Y = y, SizeMm = 40, Views = views,
        Status = HoldProposalStatus.Pending,
    };
}
