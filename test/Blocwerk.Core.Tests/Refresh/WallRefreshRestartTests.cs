// <copyright file="WallRefreshRestartTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// A restart in the middle of a step picks the run up where it was: a staged update it had not recorded yet is
/// adopted rather than refused, a lost video does not stop the 3D capture or the panel update, and an update that
/// was already promoted still gets its holds placed.
/// </summary>
public class WallRefreshRestartTests
{
    [Fact]
    public async Task AStagedUpdateNotYetRecorded_IsAdoptedOnRestart()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var id = await StartedRunAsync(s);

        // The worker staged the panel update, then stopped before it recorded the session.
        await SetAsync(h, id, r => RefreshTimeline.Set(r, RefreshTimeline.Detect, RefreshStepState.Running));
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, [new BigUpdatePhoto(CaptureScenario.TinyJpeg(70), "image/jpeg", 0, 0)]);
        var staged = await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId);
        await s.RunQueuedAsync();

        var view = await s.CurrentAsync();
        Assert.True(view.Status == WallRefreshStatus.ReadyToApply, view.Error);
        Assert.Equal(staged!.Id, view.UpdateSessionId);
    }

    [Fact]
    public async Task AVideoGoneFromDisk_DoesNotStopThe3DCaptureOrThePanels()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        s.Capture.SplatClient.IsConfigured = true;
        var id = await s.DropPhotosAsync();
        var video = await s.Service.AddVideoAsync(id, "walk.mp4", new MemoryStream([1, 2, 3]), CancellationToken.None);
        File.Delete(s.Capture.Files.ResolvePhysicalPath(video.StoredName)!);

        await s.Service.SortAsync(id);
        await s.RunQueuedAsync();
        var sorted = await s.CurrentAsync();
        await s.Service.StartAsync(id, sorted.Picks.Select(p => new PanelChoice(p.Col, p.Row, p.PhotoId)).ToList());
        await s.RunQueuedAsync();

        var view = await s.CurrentAsync();
        Assert.True(view.Status == WallRefreshStatus.ReadyToApply, view.Error);
        var capture = view.Steps.Single(st => st.Key == RefreshTimeline.Capture);
        Assert.True(capture.State == RefreshStepState.Done, capture.Detail);
        Assert.Contains("gone", capture.Detail);
    }

    [Fact]
    public async Task AnUpdatePromotedBeforeARestart_StillGetsItsHoldsPlaced()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        s.Placement.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new HoldPlacementStatus(true, true, null));
        s.Placement.PlaceAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HoldPlacementResult(Guid.NewGuid(), 2, 0, 0, []));
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();

        // The apply promoted, then the worker stopped before it finished.
        var decisions = await WallUpdateSessionFixture.Sessions(h).GetDecisionsAsync(h.WallId);
        await WallUpdateSessionFixture.BigUpdate(h).PromoteAsync(h.WallId, decisions, view.UpdateSessionId);
        await SetAsync(h, view.Id, r => r.Status = WallRefreshStatus.Applying);
        await s.Processor.ProcessAsync(view.Id, CancellationToken.None);

        var done = await s.CurrentAsync();
        Assert.Equal(WallRefreshStatus.Done, done.Status);
        Assert.Equal(RefreshStepState.Done, done.Steps.Single(st => st.Key == RefreshTimeline.Apply).State);
        await s.Placement.Received(1).PlaceAsync(h.WallId, WallRefreshProcessor.PlacementTrigger, Arg.Any<CancellationToken>());
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

    private static async Task SetAsync(WallTestHarness h, Guid id, Action<WallRefresh> change)
    {
        await using var db = h.CreateContext();
        change(await db.WallRefreshes.SingleAsync(r => r.Id == id));
        await db.SaveChangesAsync();
    }
}
