// <copyright file="WallRefreshCleanupTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// What a run holds is released: videos are capped and let go when the run fails or is discarded, a failed run
/// leaves no staged update behind, and a run left alone for a day is discarded with its draft.
/// </summary>
public class WallRefreshCleanupTests
{
    [Fact]
    public async Task Videos_AreCappedPerRun()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.SplatClient.IsConfigured = true;
        await s.SeedWallAsync();
        var view = await s.Service.BeginAsync(h.WallId);
        for (var i = 0; i < WallRefreshService.MaxVideos; i++)
        {
            await s.Service.AddVideoAsync(view.Id, $"walk{i}.mp4", new MemoryStream([1, 2, 3]), CancellationToken.None);
        }

        var ex = await Assert.ThrowsAsync<UserFacingException>(
            () => s.Service.AddVideoAsync(view.Id, "one-more.mp4", new MemoryStream([1, 2, 3]), CancellationToken.None));
        Assert.Contains($"at most {WallRefreshService.MaxVideos}", ex.Message);
        Assert.Equal(WallRefreshService.MaxVideos, (await s.CurrentAsync()).Videos.Count);
    }

    [Fact]
    public async Task AFailedRun_LetsGoOfItsVideos_AndCanBeDiscarded()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.SplatClient.IsConfigured = true;
        await s.SeedWallAsync();
        var view = await s.Service.BeginAsync(h.WallId);
        var video = await s.Service.AddVideoAsync(view.Id, "walk.mp4", new MemoryStream([1, 2, 3]), CancellationToken.None);
        await using (var db = h.CreateContext())
        {
            Assert.Contains(video.StoredName, await RefreshTimeline.StoredVideosAsync(db, CancellationToken.None));
            await db.WallRefreshes.Where(r => r.Id == view.Id).ExecuteUpdateAsync(u => u.SetProperty(r => r.Status, WallRefreshStatus.Failed));
            Assert.DoesNotContain(video.StoredName, await RefreshTimeline.StoredVideosAsync(db, CancellationToken.None));
        }

        await s.Service.DiscardAsync(view.Id);

        var path = s.Capture.Files.ResolvePhysicalPath(video.StoredName);
        Assert.False(path is not null && File.Exists(path), "the video file is deleted");
        await using var check = h.CreateContext();
        Assert.Equal(WallRefreshStatus.Discarded, (await check.WallRefreshes.SingleAsync()).Status);
    }

    [Fact]
    public async Task ARunThatFailsAfterStaging_LeavesNoOpenUpdate()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        s.Decorate = actors => actors with { Sessions = new FailingDecisionSessions(actors.Sessions) };
        await s.SeedWallAsync();

        var view = await s.PrepareAsync();

        Assert.Equal(WallRefreshStatus.Failed, view.Status);
        Assert.Contains("could not be saved", view.Error);
        Assert.Null(await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId));
        s.Decorate = actors => actors;
        Assert.NotEqual(view.Id, (await s.Service.BeginAsync(h.WallId)).Id);
    }

    [Fact]
    public async Task ARunLeftAloneForADay_IsDiscarded_WithItsDraft()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        var id = await s.DropPhotosAsync();
        await using (var db = h.CreateContext())
        {
            var row = await db.WallRefreshes.SingleAsync(r => r.Id == id);
            row.UpdatedAt = DateTimeOffset.UtcNow.AddHours(-25);
            await db.SaveChangesAsync();
        }

        Assert.Equal(1, await s.Processor.DiscardStaleAsync(DateTimeOffset.UtcNow, CancellationToken.None));

        await using var check = h.CreateContext();
        Assert.Equal(WallRefreshStatus.Discarded, (await check.WallRefreshes.SingleAsync()).Status);
        Assert.False(await check.WallCaptures.AnyAsync());
        Assert.Null(await s.Service.GetCurrentAsync(h.WallId));
    }

    [Fact]
    public async Task ARecentRun_IsNotDiscarded()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        await s.DropPhotosAsync();

        Assert.Equal(0, await s.Processor.DiscardStaleAsync(DateTimeOffset.UtcNow, CancellationToken.None));
    }
}
