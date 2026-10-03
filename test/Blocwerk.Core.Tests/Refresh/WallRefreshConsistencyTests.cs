// <copyright file="WallRefreshConsistencyTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// The confirm screen and the update session stay in step: Apply promotes exactly what the confirmed summary describes
/// (or shows the new summary first), the summary's stamp is the session's own, and work in the full review keeps a
/// prepared update from being swept as idle.
/// </summary>
public class WallRefreshConsistencyTests
{
    [Fact]
    public async Task TheSummaryStamp_IsTheSessionsStampAsWrittenWithTheDecisions()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();

        var view = await s.PrepareAsync();

        var session = await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId);
        Assert.Equal(session!.UpdatedAt, view.Summary!.DecisionsRecordedAt);
        Assert.NotNull(view.Summary.DecisionsVersion);
    }

    [Fact]
    public async Task AChangeInTheFullReview_IsNotAppliedUnseen_ButShownFirst()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        Assert.Equal(0, view.Summary!.Removed);
        await WallUpdateSessionFixture.Sessions(h).SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(old[0].Id, CarryKind.Removed, null));

        await s.Service.ApplyAsync(view.Id, view.Summary.DecisionsVersion);
        await s.RunQueuedAsync();

        var back = await s.CurrentAsync();
        Assert.Equal(WallRefreshStatus.ReadyToApply, back.Status);
        Assert.Equal(WallRefreshProcessor.EditedSinceSummary, back.Error);
        Assert.Equal(1, back.Summary!.Removed);
        Assert.True(back.Summary.EditedInFullReview);
        Assert.NotEqual(view.Summary.DecisionsVersion, back.Summary.DecisionsVersion);
        Assert.Equal(0, await GenerationAsync(h));

        await s.Service.ApplyAsync(view.Id, back.Summary.DecisionsVersion);
        await s.RunQueuedAsync();

        Assert.Equal(WallRefreshStatus.Done, (await s.CurrentAsync()).Status);
        Assert.Equal(1, await GenerationAsync(h));
        await using var db = h.CreateContext();
        Assert.Equal(old.Count - 1, await db.Holds.CountAsync(x => x.WallId == h.WallId && x.Generation == 1));
    }

    [Fact]
    public async Task ASessionTouchedWithoutAChange_AppliesAsConfirmed()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        await WallUpdateSessionFixture.Sessions(h).SetPhaseAsync(h.WallId, WallUpdatePhase.Carryover);

        await s.Service.ApplyAsync(view.Id, view.Summary!.DecisionsVersion);
        await s.RunQueuedAsync();

        Assert.Equal(WallRefreshStatus.Done, (await s.CurrentAsync()).Status);
    }

    [Fact]
    public async Task ApplyingAnOutdatedSummary_IsRefused()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();

        await Assert.ThrowsAsync<UserFacingException>(() => s.Service.ApplyAsync(view.Id, "not-the-version-on-screen"));
        Assert.Equal(WallRefreshStatus.ReadyToApply, (await s.CurrentAsync()).Status);
    }

    [Fact]
    public async Task APreparedUpdate_WorkedOnInTheFullReview_IsNotSweptAsIdle()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        await AgeRunAsync(h, view.Id);
        await WallUpdateSessionFixture.Sessions(h).SetPhaseAsync(h.WallId, WallUpdatePhase.Carryover);

        Assert.Equal(0, await s.Processor.DiscardStaleAsync(DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Equal(WallRefreshStatus.ReadyToApply, (await s.CurrentAsync()).Status);
        Assert.Equal(view.UpdateSessionId, (await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId))?.Id);
    }

    [Fact]
    public async Task APreparedUpdate_LeftAloneForADay_IsSwept()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        await AgeRunAsync(h, view.Id);
        await using (var db = h.CreateContext())
        {
            var session = await db.WallUpdateSessions.SingleAsync(x => x.Id == view.UpdateSessionId);
            session.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-25);
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await s.Processor.DiscardStaleAsync(DateTimeOffset.UtcNow, CancellationToken.None));

        Assert.Null(await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId));
    }

    private static async Task AgeRunAsync(WallTestHarness h, Guid refreshId)
    {
        await using var db = h.CreateContext();
        var row = await db.WallRefreshes.SingleAsync(r => r.Id == refreshId);
        row.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-25);
        await db.SaveChangesAsync();
    }

    private static async Task<int> GenerationAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.Walls.Where(w => w.Id == h.WallId).Select(w => w.CurrentGeneration).SingleAsync();
    }
}
