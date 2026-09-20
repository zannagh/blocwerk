using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The session HEADER: that staging opens one, that a second admin cannot silently destroy it, and that
/// promote and discard close it and leave no change-journal batch open behind them.
/// </summary>
public class WallUpdateSessionLifecycleTests
{
    [Fact]
    public async Task Stage_OpensSession_AtStagedGeneration_OwnedByStagingUser_AtDetectedPhase()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);

        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());

        var info = await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId);
        Assert.NotNull(info);
        Assert.Equal(1, info!.StagedGeneration);
        Assert.Equal(WallUpdatePhase.Detected, info.Phase);
        Assert.Equal(0, info.NeighbourIndex);
        Assert.Equal(h.Owner.Id, info.CreatedByUserId);
        Assert.Equal(h.Owner.Id, info.LastActiveByUserId);
    }

    [Fact]
    public async Task SetPhase_MovesTheResumeCursorAndRecordsWhoTouchedItLast()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentreAndNeighbour());

        // A second wall admin picks the flow up — sessions are per wall, not per user.
        var helper = await h.AddMemberAsync("helper@test", WallRole.Admin);
        h.ActingUser = helper;
        var info = await WallUpdateSessionFixture.Sessions(h)
            .SetPhaseAsync(h.WallId, WallUpdatePhase.Neighbours, neighbourIndex: 1);

        Assert.Equal(WallUpdatePhase.Neighbours, info.Phase);
        Assert.Equal(1, info.NeighbourIndex);
        Assert.Equal(h.Owner.Id, info.CreatedByUserId);
        Assert.Equal(helper.Id, info.LastActiveByUserId);
    }

    [Fact]
    public async Task SecondStage_RefusesAndLeavesTheFirstAdminsStagedWorkIntact()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);
        var service = WallUpdateSessionFixture.BigUpdate(h);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var firstPanelId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var stagedHoldId = await WallUpdateSessionFixture.AddStagedHoldAsync(h, firstPanelId, stagedGen: 1);

        var helper = await h.AddMemberAsync("helper@test", WallRole.Admin);
        h.ActingUser = helper;
        var conflict = await Assert.ThrowsAsync<WallUpdateSessionConflictException>(
            () => service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto()));

        Assert.Equal(h.Owner.Id, conflict.Existing.CreatedByUserId);
        await using var db = h.CreateContext();
        Assert.True(await db.WallPanels.AnyAsync(p => p.Id == firstPanelId && p.StagedPhoto != null));
        Assert.True(await db.Holds.AnyAsync(x => x.Id == stagedHoldId));
        Assert.Equal(1, await db.WallUpdateSessions.CountAsync(s => s.Status == WallUpdateSessionStatus.Open));
    }

    [Fact]
    public async Task Takeover_DiscardsTheSupersededSessionAndOpensAFreshOne()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);

        // Wired to a REAL change journal: half of what a takeover has to do is sealing the abandoned
        // update's batch, and an unwired journal exercises none of it.
        var journal = new ChangeJournal(() => h.CreateContext());
        var service = WallUpdateSessionFixture.BigUpdate(h, journal);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var firstPanelId = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);

        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto(), takeOverExisting: true);

        await using var db = h.CreateContext();
        Assert.False(await db.WallPanels.AnyAsync(p => p.Id == firstPanelId));
        Assert.Equal(1, await db.WallUpdateSessions.CountAsync(s => s.Status == WallUpdateSessionStatus.Discarded));
        Assert.Equal(1, await db.WallUpdateSessions.CountAsync(s => s.Status == WallUpdateSessionStatus.Open));

        // Three distinct batches, in order: the abandoned update's, the takeover's own clear-down, and
        // the new update's — the first two SEALED and only the last open. Sealing is the whole point:
        // an open batch is RESUMED by the next staging, so without it the abandoned update, its deletion
        // and the replacement update would be one batch that could never be reverted apart.
        var batches = await db.ChangeJournalBatches
            .Where(b => b.ScopeId == h.WallId && b.Label == ChangeJournal.WallUpdateBatchLabel)
            .OrderBy(b => b.CreatedAt)
            .ToListAsync();
        Assert.Equal(3, batches.Count);
        Assert.All(batches.Take(2), b => Assert.NotNull(b.SealedAt));
        Assert.Null(batches[2].SealedAt);
    }

    [Fact]
    public async Task Discard_ClosesTheSessionAndSealsTheWallUpdateBatch()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);
        var journal = new ChangeJournal(() => h.CreateContext());
        var service = WallUpdateSessionFixture.BigUpdate(h, journal);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());

        await service.DiscardAsync(h.WallId);

        await using var db = h.CreateContext();
        var session = await db.WallUpdateSessions.SingleAsync();
        Assert.Equal(WallUpdateSessionStatus.Discarded, session.Status);
        Assert.NotNull(session.ClosedAt);
        Assert.False(await db.ChangeJournalBatches.AnyAsync(b => b.SealedAt == null));
    }

    [Fact]
    public async Task Promote_ClosesTheSessionAsPromotedAndSealsTheWallUpdateBatch()
    {
        using var h = new WallTestHarness();
        var old = (await h.SeedWallAsync(holdCount: 1))[0];
        WallUpdateSessionFixture.NoDetections(h);
        var journal = new ChangeJournal(() => h.CreateContext());
        var service = WallUpdateSessionFixture.BigUpdate(h, journal);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());

        await service.PromoteAsync(
            h.WallId,
            new BigUpdateConfirmation([new CarryoverDecision(old.Id, CarryKind.Carried, null)], [], [], []));

        await using var db = h.CreateContext();
        var session = await db.WallUpdateSessions.SingleAsync();
        Assert.Equal(WallUpdateSessionStatus.Promoted, session.Status);
        Assert.NotNull(session.ClosedAt);
        Assert.False(await db.ChangeJournalBatches.AnyAsync(b => b.SealedAt == null));
    }

    [Fact]
    public async Task StagedResidueWithNoSessionRow_IsStillClearedByANewUpdate()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);
        var service = WallUpdateSessionFixture.BigUpdate(h);
        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());

        // Simulate an update staged before sessions existed: the staged panels remain, the session does not.
        await using (var db = h.CreateContext())
        {
            db.WallUpdateSessions.RemoveRange(await db.WallUpdateSessions.ToListAsync());
            await db.SaveChangesAsync();
        }

        await service.StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());

        await using var check = h.CreateContext();
        Assert.Equal(1, await check.WallPanels.CountAsync(p => p.WallId == h.WallId && p.StagedPhoto != null));
        Assert.Equal(1, await check.WallUpdateSessions.CountAsync(s => s.Status == WallUpdateSessionStatus.Open));
    }
}
