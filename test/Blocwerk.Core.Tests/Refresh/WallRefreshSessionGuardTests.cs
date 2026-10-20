// <copyright file="WallRefreshSessionGuardTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// "Update panels + 3D" next to a step-by-step update on the same wall (review C3, C5): a run never adopts an update
/// the person started in the wizard, never writes its defaults over choices a person made, refuses to start (before
/// anything runs) while such an update is open, and a step-by-step update forgotten for a week is swept.
/// </summary>
public class WallRefreshSessionGuardTests
{
    [Fact]
    public async Task AStepByStepUpdateStartedWhileARunWaits_IsNotAdopted_AndItsChoicesStay()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        await StartedRunAsync(s);

        // Same admin, on another device: starts "Update step by step" and removes a hold, then the run's worker starts.
        var wizard = await WizardWithRemovalAsync(h, old[0].Id);
        await s.RunQueuedAsync();

        var view = await s.CurrentAsync();
        Assert.NotEqual(WallRefreshStatus.ReadyToApply, view.Status);
        Assert.NotEqual(wizard, view.UpdateSessionId);
        Assert.Equal(wizard, (await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId))?.Id);
        await AssertRemovedAsync(h, old[0].Id);
    }

    [Fact]
    public async Task AnAdoptedStagingAPersonAlreadyDecidedOn_KeepsTheirChoice()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var id = await StartedRunAsync(s);

        // The worker staged, then stopped before it recorded the session; the admin opened the full review meanwhile.
        await SetAsync(h, id, r => RefreshTimeline.Set(r, RefreshTimeline.Detect, RefreshStepState.Running));
        var staged = await WizardWithRemovalAsync(h, old[0].Id);
        await s.RunQueuedAsync();

        var view = await s.CurrentAsync();
        Assert.True(view.Status == WallRefreshStatus.ReadyToApply, view.Error);
        Assert.Equal(staged, view.UpdateSessionId);
        Assert.True(view.Summary!.EditedInFullReview);
        await AssertRemovedAsync(h, old[0].Id);
    }

    [Fact]
    public async Task StartingWhileAStepByStepUpdateIsOpen_IsRefused_BeforeAnythingRuns()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        var id = await s.DropPhotosAsync();
        await s.Service.SortAsync(id);
        await s.RunQueuedAsync();
        var sorted = await s.CurrentAsync();
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, [new BigUpdatePhoto(CaptureScenario.TinyJpeg(70), "image/jpeg", 0, 0)]);

        var refused = await Assert.ThrowsAsync<UserFacingException>(() =>
            s.Service.StartAsync(id, sorted.Picks.Select(p => new PanelChoice(p.Col, p.Row, p.PhotoId)).ToList()));

        Assert.Contains("already has an update in progress", refused.Message);
        var view = await s.CurrentAsync();
        Assert.Equal(WallRefreshStatus.ReadyToStart, view.Status);
        Assert.NotEqual(RefreshStepState.Running, view.Steps.Single(st => st.Key == RefreshTimeline.Capture).State);
        await using var db = h.CreateContext();
        Assert.False((await db.WallRefreshes.SingleAsync(r => r.Id == id)).CaptureStarted);
    }

    [Fact]
    public async Task AStepByStepUpdateUntouchedForAWeek_IsDiscarded_ARecentOneIsKept()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, [new BigUpdatePhoto(CaptureScenario.TinyJpeg(70), "image/jpeg", 0, 0)]);

        Assert.Equal(0, await s.Processor.DiscardIdleSessionsAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        await AgeSessionsAsync(h, TimeSpan.FromDays(8));
        Assert.Equal(1, await s.Processor.DiscardIdleSessionsAsync(DateTimeOffset.UtcNow, CancellationToken.None));

        await using var db = h.CreateContext();
        Assert.Equal(WallUpdateSessionStatus.Discarded, (await db.WallUpdateSessions.SingleAsync()).Status);
        Assert.False(await db.WallPanels.AnyAsync(p => p.StagedPhoto != null));
    }

    [Fact]
    public async Task AnIdleSessionARunStillOwns_IsLeftToTheRunsOwnSweep()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        await AgeSessionsAsync(h, TimeSpan.FromDays(8));

        Assert.Equal(0, await s.Processor.DiscardIdleSessionsAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        Assert.Equal(view.UpdateSessionId, (await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId))?.Id);
    }

    private static async Task<Guid> WizardWithRemovalAsync(WallTestHarness h, Guid oldHoldId)
    {
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, [new BigUpdatePhoto(CaptureScenario.TinyJpeg(70), "image/jpeg", 0, 0)]);
        var sessions = WallUpdateSessionFixture.Sessions(h);
        var session = (await sessions.GetOpenSessionAsync(h.WallId))!.Id;
        await Task.Delay(5);
        await sessions.SaveCarryDecisionAsync(h.WallId, new CarryoverDecision(oldHoldId, CarryKind.Removed, null, Confirmed: true), session);
        return session;
    }

    private static async Task AssertRemovedAsync(WallTestHarness h, Guid oldHoldId)
    {
        var decisions = await WallUpdateSessionFixture.Sessions(h).GetDecisionsAsync(h.WallId);
        var decision = decisions.Carryover.Single(d => d.OldHoldId == oldHoldId);
        Assert.Equal((CarryKind.Removed, true), (decision.Kind, decision.Confirmed));
    }

    private static async Task<Guid> StartedRunAsync(RefreshScenario s)
    {
        var id = await s.DropPhotosAsync();
        await s.Service.SortAsync(id);
        await s.RunQueuedAsync();
        var sorted = await s.CurrentAsync();
        await s.Service.StartAsync(id, sorted.Picks.Select(p => new PanelChoice(p.Col, p.Row, p.PhotoId)).ToList());
        return id;
    }

    private static async Task AgeSessionsAsync(WallTestHarness h, TimeSpan by)
    {
        await using var db = h.CreateContext();
        foreach (var session in await db.WallUpdateSessions.ToListAsync())
        {
            session.UpdatedAt -= by;
        }

        await db.SaveChangesAsync();
    }

    private static async Task SetAsync(WallTestHarness h, Guid id, Action<WallRefresh> change)
    {
        await using var db = h.CreateContext();
        change(await db.WallRefreshes.SingleAsync(r => r.Id == id));
        await db.SaveChangesAsync();
    }
}
