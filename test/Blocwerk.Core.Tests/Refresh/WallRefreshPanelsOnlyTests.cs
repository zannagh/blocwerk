// <copyright file="WallRefreshPanelsOnlyTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// "Update panels only (keep the 3D model)": on a wall where 3D capture would be available, the panel update runs
/// without a capture or a runner job, the active model stays as it is, and the new generation's holds are placed on its
/// textures in the background.
/// </summary>
public class WallRefreshPanelsOnlyTests
{
    [Fact]
    public async Task PanelsOnly_KeepsTheActiveModel_StartsNoCapture_AndQueuesNoRunnerJob()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        var modelId = await AddActiveModelAsync(h);
        var captures = await CaptureCountAsync(h);

        var view = await PrepareKeepingModelAsync(s);

        Assert.Equal(WallRefreshStatus.ReadyToApply, view.Status);
        var capture = view.Steps.Single(st => st.Key == RefreshTimeline.Capture);
        Assert.Equal(RefreshStepState.Skipped, capture.State);
        Assert.Equal(WallRefreshService.KeptModelNote, capture.Detail);
        Assert.Null(view.Capture);
        Assert.Equal(captures, await CaptureCountAsync(h));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => s.Capture.Queue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(100)).Token).AsTask());
        Assert.Equal(modelId, await ActiveModelIdAsync(h));
    }

    [Fact]
    public async Task PanelsOnly_AfterTheConfirm_PlacesTheNewGenerationOnTheOldModel_WithoutCapture()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Placement.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new HoldPlacementStatus(true, true, null));
        s.Placement.PlaceAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new HoldPlacementResult(Guid.NewGuid(), 1, 1, 0, []));
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        var modelId = await AddActiveModelAsync(h);
        var view = await PrepareKeepingModelAsync(s);

        await s.Service.ApplyAsync(view.Id);
        await s.RunQueuedAsync();

        var done = await s.CurrentAsync();
        Assert.Equal(WallRefreshStatus.Done, done.Status);
        Assert.Equal(1, await GenerationAsync(h));
        await s.Placement.Received(1).PlaceAsync(h.WallId, WallRefreshProcessor.PlacementTrigger, Arg.Any<CancellationToken>());
        var place = done.Steps.Single(st => st.Key == RefreshTimeline.Place);
        Assert.Equal(RefreshStepState.Done, place.State);
        Assert.Equal(modelId, await ActiveModelIdAsync(h));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => s.Capture.Queue.DequeueAsync(new CancellationTokenSource(TimeSpan.FromMilliseconds(100)).Token).AsTask());
    }

    [Fact]
    public async Task PanelsOnly_WhenPlacementFails_TheUpdateStillGoesLive_And3DStaysAsItWas()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Placement.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new HoldPlacementStatus(true, true, null));
        s.Placement.PlaceAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<HoldPlacementResult>>(_ => throw new InvalidOperationException("no registration"));
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        var modelId = await AddActiveModelAsync(h);
        var view = await PrepareKeepingModelAsync(s);

        await s.Service.ApplyAsync(view.Id);
        await s.RunQueuedAsync();

        var done = await s.CurrentAsync();
        Assert.Equal(WallRefreshStatus.Done, done.Status);
        Assert.Equal(1, await GenerationAsync(h));
        Assert.Equal(RefreshStepState.Failed, done.Steps.Single(st => st.Key == RefreshTimeline.Place).State);
        Assert.Equal(modelId, await ActiveModelIdAsync(h));
    }

    private static async Task<WallRefreshView> PrepareKeepingModelAsync(RefreshScenario s)
    {
        var id = await s.DropPhotosAsync();
        await s.Service.SortAsync(id);
        await s.RunQueuedAsync();
        var sorted = await s.CurrentAsync();
        Assert.True(sorted.CaptureAvailable, "the wall could capture 3D, so keeping the model is a real choice");
        await s.Service.StartAsync(id, sorted.Picks.Select(p => new PanelChoice(p.Col, p.Row, p.PhotoId)).ToList(), keepModel: true);
        await s.RunQueuedAsync();
        return await s.CurrentAsync();
    }

    private static async Task<Guid> AddActiveModelAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        var model = new WallGeometryModel
        {
            WallId = h.WallId, Json = "{}", SchemaVersion = 1, Source = "glyph-solver v1", IsActive = true,
        };
        db.WallGeometryModels.Add(model);
        await db.SaveChangesAsync();
        return model.Id;
    }

    private static async Task<Guid> ActiveModelIdAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallGeometryModels.Where(m => m.WallId == h.WallId && m.IsActive).Select(m => m.Id).SingleAsync();
    }

    private static async Task<int> CaptureCountAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.WallCaptures.CountAsync(c => c.WallId == h.WallId);
    }

    private static async Task<int> GenerationAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.Walls.Where(w => w.Id == h.WallId).Select(w => w.CurrentGeneration).SingleAsync();
    }
}
