// <copyright file="WallRefreshApplyRaceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.Automation;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// Apply against a 3D re-check: the background apply promotes only the version the user CONFIRMED (stored on the run),
/// never a summary rewritten after the confirm, and the user's Apply and the worker's steps of a wall never interleave.
/// </summary>
public class WallRefreshApplyRaceTests
{
    [Fact]
    public async Task AcceptingApply_StoresTheConfirmedVersion()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();

        await s.Service.ApplyAsync(view.Id, view.Summary!.DecisionsVersion);

        var row = await RowAsync(h, view.Id);
        Assert.Equal(WallRefreshStatus.Applying, row.Status);
        Assert.Equal(view.Summary.DecisionsVersion, row.ConfirmedDecisionsVersion);
    }

    /// <summary>
    /// The interleaving the shared lock now prevents, set up directly: the user confirmed v1, then a re-check recorded other
    /// decisions and rewrote the summary to describe them (v2) before the worker applied. Comparing with the stored summary
    /// would promote v2, which nobody saw; comparing with the confirmed v1 refuses and shows v2 first.
    /// </summary>
    [Fact]
    public async Task ASummaryRewrittenAfterTheConfirm_IsNotApplied_ButShownFirst()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        var confirmed = view.Summary!.DecisionsVersion!;
        await WallUpdateSessionFixture.Sessions(h).SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(old[0].Id, CarryKind.Removed, null));
        await s.Service.ApplyAsync(view.Id, confirmed);
        await s.RunQueuedAsync();
        var rewritten = (await s.CurrentAsync()).Summary!.DecisionsVersion;
        Assert.NotEqual(confirmed, rewritten);
        await SetAsync(h, view.Id, r =>
        {
            r.Status = WallRefreshStatus.Applying;
            r.ConfirmedDecisionsVersion = confirmed;
        });

        await s.Processor.ProcessAsync(view.Id, CancellationToken.None);

        var back = await s.CurrentAsync();
        Assert.Equal(WallRefreshStatus.ReadyToApply, back.Status);
        Assert.Equal(rewritten, back.Summary!.DecisionsVersion);
        Assert.Null((await RowAsync(h, view.Id)).ConfirmedDecisionsVersion);
        Assert.Equal(0, await RefreshApiFlow.GenerationAsync(h));
    }

    [Fact]
    public async Task Apply_WhileTheWallsBackgroundStepRuns_IsRefused_AndGoesThroughAfterwards()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();

        using (await s.Locks.AcquireAsync(h.WallId, CancellationToken.None))
        {
            var refused = await Assert.ThrowsAsync<UserFacingException>(() => s.Service.ApplyAsync(view.Id, view.Summary!.DecisionsVersion));
            Assert.Equal(WallRefreshService.BeingChecked, refused.Message);
            Assert.Equal(WallRefreshStatus.ReadyToApply, (await RowAsync(h, view.Id)).Status);
        }

        await s.Service.ApplyAsync(view.Id, view.Summary!.DecisionsVersion);
        await s.RunQueuedAsync();

        Assert.Equal(WallRefreshStatus.Done, (await s.CurrentAsync()).Status);
        Assert.Equal(1, await RefreshApiFlow.GenerationAsync(h));
    }

    [Fact]
    public async Task TheWorker_WaitsForAnApplyInProgress_ThenAppliesInsteadOfRechecking()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();

        Task step;
        using (await s.Locks.AcquireAsync(h.WallId, CancellationToken.None))
        {
            // The worker picks the run up (a re-check would be due) while an Apply holds the wall.
            step = s.Processor.ProcessAsync(view.Id, CancellationToken.None);
            await SetAsync(h, view.Id, r =>
            {
                r.Status = WallRefreshStatus.Applying;
                r.ConfirmedDecisionsVersion = view.Summary!.DecisionsVersion;
            });
            Assert.False(step.IsCompleted);
        }

        await step;

        Assert.Equal(WallRefreshStatus.Done, (await s.CurrentAsync()).Status);
        Assert.Equal(1, await RefreshApiFlow.GenerationAsync(h));
    }

    private static async Task<WallRefresh> RowAsync(WallTestHarness h, Guid id)
    {
        await using var db = h.CreateContext();
        return await db.WallRefreshes.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    private static async Task SetAsync(WallTestHarness h, Guid id, Action<WallRefresh> change)
    {
        await using var db = h.CreateContext();
        change(await db.WallRefreshes.SingleAsync(r => r.Id == id));
        await db.SaveChangesAsync();
    }
}
