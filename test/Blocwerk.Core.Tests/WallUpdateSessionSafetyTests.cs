using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The multi-admin hazards of the resumable wall update: a stale circuit acting on an update that has
/// been replaced, two admins starting an update at the same moment, and a change-journal batch left
/// open by a crashed promote being inherited by the next, unrelated update.
/// </summary>
public class WallUpdateSessionSafetyTests
{
    [Fact]
    public async Task StalePromote_AfterATakeover_IsRefusedAndLeavesTheNewStagingIntact()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        var service = WallUpdateSessionFixture.BigUpdate(h);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());

        // Admin A is sitting on Confirm with THIS session id in their browser.
        var stale = (await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId))!.Id;

        // Admin B takes the wall over: A's staged panels are gone and a fresh session is open.
        var helper = await h.AddMemberAsync("helper@test", WallRole.Admin);
        h.ActingUser = helper;
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto(), takeOverExisting: true);
        var newPanelId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);

        // A presses Apply. Without the identity check this promoted B's photos under A's decisions.
        h.ActingUser = h.Owner;
        var refused = await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(() => service.PromoteAsync(
            h.WallId,
            new BigUpdateConfirmation([new CarryoverDecision(old.Id, CarryKind.Removed, null)], [], [], []),
            stale));
        Assert.Equal(stale, refused.ExpectedSessionId);
        Assert.NotEqual(stale, refused.CurrentSessionId);

        await using var db = h.CreateContext();
        Assert.True(await db.WallPanels.AnyAsync(p => p.Id == newPanelId && p.StagedPhoto != null));
        Assert.Equal(0, (await db.Walls.SingleAsync(w => w.Id == h.WallId)).CurrentGeneration);
        Assert.False((await db.Holds.SingleAsync(x => x.Id == old.Id)).Generation != 0);
    }

    [Fact]
    public async Task StaleDiscard_AfterATakeover_IsRefusedAndLeavesTheNewStagingIntact()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);
        var service = WallUpdateSessionFixture.BigUpdate(h);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var stale = (await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId))!.Id;

        var helper = await h.AddMemberAsync("helper@test", WallRole.Admin);
        h.ActingUser = helper;
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto(), takeOverExisting: true);
        var newPanelId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);

        h.ActingUser = h.Owner;
        await Assert.ThrowsAsync<WallUpdateSessionSupersededException>(
            () => service.DiscardAsync(h.WallId, stale));

        await using var db = h.CreateContext();
        Assert.True(await db.WallPanels.AnyAsync(p => p.Id == newPanelId && p.StagedPhoto != null));
        Assert.Equal(1, await db.WallUpdateSessions.CountAsync(s => s.Status == WallUpdateSessionStatus.Open));
    }

    [Fact]
    public async Task PromoteAndDiscard_NamingTheWallsOwnOpenSession_AreAllowed()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        var service = WallUpdateSessionFixture.BigUpdate(h);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var mine = (await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId))!.Id;

        await service.PromoteAsync(
            h.WallId,
            new BigUpdateConfirmation([new CarryoverDecision(old.Id, CarryKind.Carried, null)], [], [], []),
            mine);

        await using var db = h.CreateContext();
        Assert.Equal(1, (await db.Walls.SingleAsync(w => w.Id == h.WallId)).CurrentGeneration);
        Assert.Equal(WallUpdateSessionStatus.Promoted, (await db.WallUpdateSessions.SingleAsync()).Status);
    }

    [Fact]
    public async Task TwoOpenSessionsForOneWall_AreRejectedByTheStore()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);

        await using (var first = h.CreateContext())
        {
            WallUpdateSessions.Open(first, h.WallId, 1, h.Owner.Id);
            await first.SaveChangesAsync();
        }

        // A second context, as a second admin's request would be. "At most one open session per wall"
        // has to be a constraint, not a comment: nothing in the service path holds a lock across it.
        await using var second = h.CreateContext();
        WallUpdateSessions.Open(second, h.WallId, 1, h.Owner.Id);
        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveChangesAsync());

        await using var check = h.CreateContext();
        Assert.Equal(1, await check.WallUpdateSessions.CountAsync(s => s.Status == WallUpdateSessionStatus.Open));
    }

    [Fact]
    public async Task ClosedSessionsDoNotBlockANewOne_TheUniqueIndexCoversOpenOnly()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);

        await using var db = h.CreateContext();
        foreach (var status in new[] { WallUpdateSessionStatus.Promoted, WallUpdateSessionStatus.Discarded })
        {
            var closed = WallUpdateSessions.Open(db, h.WallId, 1, h.Owner.Id);
            closed.Status = status;
            await db.SaveChangesAsync();
        }

        WallUpdateSessions.Open(db, h.WallId, 2, h.Owner.Id);
        await db.SaveChangesAsync();
        Assert.Equal(3, await db.WallUpdateSessions.CountAsync(s => s.WallId == h.WallId));
    }

    [Fact]
    public async Task TwoAdminsStagingAtOnce_LoseTheRaceAsAConflict_NotAsADatabaseError()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        var service = WallUpdateSessionFixture.BigUpdate(h);

        // Detection runs INSIDE StageAsync, after the open-session check and before the one SaveChanges
        // that writes the panels and the session — precisely the window the race lives in. Opening the
        // rival session from there is the interleaving, done deterministically instead of by luck.
        var raced = false;
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ =>
            {
                if (!raced)
                {
                    raced = true;
                    using var rival = h.CreateContext();
                    WallUpdateSessions.Open(rival, h.WallId, 1, h.Owner.Id);
                    rival.SaveChanges();
                }

                return Task.FromResult(new List<DetectedHold>());
            });

        var conflict = await Assert.ThrowsAsync<WallUpdateSessionConflictException>(
            () => service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto()));
        Assert.Equal(h.WallId, conflict.Existing.WallId);

        // The loser's whole staging rolled back: one open session, and no staged panel behind it.
        await using var db = h.CreateContext();
        Assert.Equal(1, await db.WallUpdateSessions.CountAsync(s => s.Status == WallUpdateSessionStatus.Open));
        Assert.False(await db.WallPanels.AnyAsync(p => p.WallId == h.WallId && p.StagedPhoto != null));
    }

    [Fact]
    public async Task AWallUpdateBatchLeftOpenByACrashedPromote_IsSealedByTheNextStaging()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        var journal = new ChangeJournal(() => h.CreateContext());
        var service = WallUpdateSessionFixture.BigUpdate(h, journal);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        await service.PromoteAsync(
            h.WallId, new BigUpdateConfirmation([new CarryoverDecision(old.Id, CarryKind.Carried, null)], [], [], []));

        // Simulate the crash window: the promote committed, the seal never ran.
        Guid crashedBatchId;
        await using (var db = h.CreateContext())
        {
            var batch = await db.ChangeJournalBatches.OrderBy(b => b.CreatedAt).LastAsync();
            batch.SealedAt = null;
            crashedBatchId = batch.Id;
            await db.SaveChangesAsync();
        }

        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());

        // The next update must open its OWN batch, or the two could never be reverted apart.
        await using var check = h.CreateContext();
        Assert.NotNull((await check.ChangeJournalBatches.SingleAsync(b => b.Id == crashedBatchId)).SealedAt);
        var open = await check.ChangeJournalBatches.Where(b => b.SealedAt == null).ToListAsync();
        Assert.Single(open);
        Assert.NotEqual(crashedBatchId, open[0].Id);
    }

    [Fact]
    public async Task ClearingLegacyResidueWithNoSession_DoesNotAppendToTheNewUpdatesBatch()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);
        var journal = new ChangeJournal(() => h.CreateContext());
        var service = WallUpdateSessionFixture.BigUpdate(h, journal);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());

        // An update staged before sessions existed: panels remain, the header does not.
        await using (var db = h.CreateContext())
        {
            db.WallUpdateSessions.RemoveRange(await db.WallUpdateSessions.ToListAsync());
            await db.SaveChangesAsync();
        }

        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());

        // The residue's clear-down gets a batch of its own, sealed; the new update opens a THIRD. Before
        // the defensive seal, the clear-down's DELETEs were appended to whatever batch was open and the
        // new update's staging then resumed that same batch — one batch holding two unrelated updates.
        await using var check = h.CreateContext();
        var batches = await check.ChangeJournalBatches
            .Where(b => b.ScopeId == h.WallId && b.Label == ChangeJournal.WallUpdateBatchLabel)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync();
        Assert.Equal(3, batches.Count);
        Assert.All(batches.Take(2), b => Assert.NotNull(b.SealedAt));
        Assert.Null(batches[2].SealedAt);
    }
}
