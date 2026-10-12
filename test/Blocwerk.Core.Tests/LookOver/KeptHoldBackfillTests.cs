// <copyright file="KeptHoldBackfillTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The startup step for a wall whose update was applied by the version before this one: the holds that update could
/// not find are marked now and each gets a finding, counted as (copies the update kept) minus (what a person confirmed
/// since); findings are added only for copies that have none. Once per wall.
/// </summary>
public class KeptHoldBackfillTests
{
    [Fact]
    public async Task AWallUpdatedBefore_GetsItsUnconfirmedHoldsMarked_AndAFindingForEachThatHadNone_Once()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        await KeptHoldFixture.ForgetKeptStateAsync(h, k);
        Assert.Single(await FindingsAsync(h, k.Wall.WallId));

        await KeptHoldBackfill.RunIfNeededAsync(h.DbContextFactory, null, NullLogger.Instance);

        // Two copies the update kept, one of which had a finding (a boulder uses it): 2 marked, 1 finding added.
        Assert.Equal(new[] { k.UnusedId, k.UsedId }.Order(), await KeptIdsAsync(h, k));
        var findings = await FindingsAsync(h, k.Wall.WallId);
        Assert.Equal(2, findings.Count);
        Assert.All(findings, f => Assert.True(f.HoldKept));
        var added = findings.Single(f => f.HoldId == k.UnusedId);
        Assert.Equal(
            (LookOverFindingKind.NotFound, LookOverFindingStatus.Open, 3, 0, (Guid?)k.UnusedOldId),
            (added.Kind, added.Status, added.Generation, added.BouldersAtApply, added.OldHoldId));
        Assert.Empty(added.MarkedBoulderIds!);
        Assert.Equal(3, (await WallAsync(h, k.Wall.WallId)).KeptHoldsGeneration);
        var service = Service(h);
        Assert.Equal((2, 2), ((await service.GetStateAsync(k.Wall.WallId))!.OpenFindings, (await service.GetStateAsync(k.Wall.WallId))!.MissingHolds));

        // A hold a person confirmed afterwards is not marked again by the next start.
        await service.AnswerAsync(k.Wall.WallId, added.Id, LookOverAnswer.Settle);
        await KeptHoldBackfill.RunIfNeededAsync(h.DbContextFactory, null, NullLogger.Instance);

        Assert.Equal([k.UsedId], await KeptIdsAsync(h, k));
        Assert.Equal(2, (await FindingsAsync(h, k.Wall.WallId)).Count);
    }

    [Fact]
    public async Task AHoldSomebodyAlreadyAnsweredStillThere_IsNotMarked_AndAHoldUnderANewHoldKeepsItsFinding()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h, covered: true);
        await Service(h).AnswerAsync(k.Wall.WallId, (await KeptHoldFixture.FindingOfAsync(h, k.UsedId)).Id, LookOverAnswer.Settle);
        await KeptHoldFixture.ForgetKeptStateAsync(h, k);

        await using (var db = h.CreateContext())
        {
            Assert.Equal((1, 0), await KeptHoldBackfill.BackfillWallAsync(db, k.Wall.WallId, CancellationToken.None));
        }

        Assert.Equal([k.UnusedId], await KeptIdsAsync(h, k));
        var covered = await KeptHoldFixture.FindingOfAsync(h, k.UnusedId);
        Assert.Equal((LookOverFindingKind.SameSpot, true, true, k.BigId), (covered.Kind, covered.HoldKept, covered.Covered, covered.OtherHoldId));
    }

    [Fact]
    public async Task TheJournal_SaysWhichHoldsTheApplyCopied_WhenItRecordedTheUpdate()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        await KeptHoldFixture.ForgetKeptStateAsync(h, k);
        await using (var db = h.CreateContext())
        {
            // Without the session's pairings every carried hold would look like a copy: the journal's times decide.
            await db.WallUpdateHoldDecisions.ExecuteDeleteAsync();
            var applied = await db.HoldGenerationLinks.Where(l => l.ToGeneration == 3).MinAsync(l => l.CreatedAt);
            var batch = new ChangeJournalBatch { Label = "wall-update", ScopeKind = ChangeJournalScopeKind.Wall, ScopeId = k.Wall.WallId };
            db.ChangeJournalBatches.Add(batch);
            var seq = 0;
            foreach (var (holdId, at) in new[] { (k.FoundId, applied.AddHours(-1)), (k.UsedId, applied.AddSeconds(1)), (k.UnusedId, applied) })
            {
                db.ChangeJournalEntries.Add(new ChangeJournalEntry
                {
                    BatchId = batch.Id, Seq = seq++, EntityType = nameof(Hold), Op = ChangeJournalOp.Insert, KeyJson = $"{{\"Id\":\"{holdId}\"}}", CreatedAt = at,
                });
            }

            await db.SaveChangesAsync();
        }

        await KeptHoldBackfill.RunIfNeededAsync(h.DbContextFactory, null, NullLogger.Instance);

        Assert.Equal(new[] { k.UnusedId, k.UsedId }.Order(), await KeptIdsAsync(h, k));
    }

    [Fact]
    public async Task AWallWhoseLookOverBelongsToAnEarlierUpdate_IsLeftAlone()
    {
        using var h = new WallTestHarness();
        var k = await KeptHoldFixture.ApplyAsync(h);
        await KeptHoldFixture.ForgetKeptStateAsync(h, k);
        await using (var db = h.CreateContext())
        {
            await db.Walls.Where(w => w.Id == k.Wall.WallId).ExecuteUpdateAsync(s => s.SetProperty(w => w.LookOverGeneration, 2));
        }

        await KeptHoldBackfill.RunIfNeededAsync(h.DbContextFactory, null, NullLogger.Instance);

        Assert.Empty(await KeptIdsAsync(h, k));
        Assert.Single(await FindingsAsync(h, k.Wall.WallId));
    }

    private static async Task<List<Guid>> KeptIdsAsync(WallTestHarness h, KeptWall k)
    {
        await using var db = h.CreateContext();
        return await db.Holds.Where(x => x.WallId == k.Wall.WallId && x.KeptUnconfirmedGeneration == 3).Select(x => x.Id).OrderBy(id => id).ToListAsync();
    }
}
