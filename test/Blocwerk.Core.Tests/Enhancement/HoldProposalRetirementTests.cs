// <copyright file="HoldProposalRetirementTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.Proposals;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>Pending proposals stay tidy: a superseded model's unseen ones and same-spot twins are retired, review items follow.</summary>
public sealed class HoldProposalRetirementTests
{
    private static readonly Guid Model = Guid.NewGuid();
    private static readonly Guid OldModel = Guid.NewGuid();

    [Fact]
    public void ANewPass_RetiresTheOldModelsUnseenProposals_KeepsTheSeenOnes_AndTheStrongerOfTwoOnOneSpot()
    {
        var oldUnseen = Pending(500, 0, OldModel);
        var oldSeen = Pending(0, 0, OldModel);
        var strong = Pending(1000, 0, Model, views: 5);
        var weak = Pending(1010, 0, Model, views: 2);
        var alone = Pending(2000, 0, Model);

        var plan = HoldProposalRetirement.Plan([oldUnseen, oldSeen, strong, weak, alone], [Candidate(3, 0), Candidate(1000, 0), Candidate(2000, 0)], Model);

        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, r => r.Retired == oldUnseen && r.Survivor is null);
        Assert.Contains(plan, r => r.Retired == weak && r.Survivor == strong);
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

    private static HoldProposalCandidate Candidate(double x, double y) =>
        new("f", 0, 0, 0, [x, y, 0], 40, 3, 0.9, 1, new CaptureDetection("p", 1, 1, 5, 0.9), []);
}
