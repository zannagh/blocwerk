// <copyright file="WallRefreshOwnershipTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// Whose a run is: its draft is not the capture panel's draft, every admin of the wall can drop files into it,
/// a wall has one open run, and the upload endpoint settles who may upload before it reads the body.
/// </summary>
public class WallRefreshOwnershipTests
{
    [Fact]
    public async Task TheRunsDraft_IsNeverTheCapturePanelsDraft()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        var view = await s.Service.BeginAsync(h.WallId);

        Assert.Null(await s.Capture.Service.GetDraftAsync(h.WallId));
        var panelDraft = await s.Capture.Service.CreateDraftAsync(h.WallId);
        Assert.NotEqual(view.CaptureId, panelDraft.CaptureId);
    }

    [Fact]
    public async Task ASecondAdmin_CanDropPhotosIntoTheRun()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        var view = await s.Service.BeginAsync(h.WallId);
        h.ActingUser = await h.AddMemberAsync("second-admin", WallRole.Admin);

        await s.Service.AddPhotoAsync(view.Id, "IMG_1.jpg", ExifJpeg.Build(CaptureScenario.TinyJpeg(60)), CancellationToken.None);

        var current = await s.CurrentAsync();
        Assert.Equal(view.Id, current.Id);
        Assert.Single(current.Photos);
    }

    [Fact]
    public async Task AWall_HasOneOpenRun()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        var first = await s.Service.BeginAsync(h.WallId);

        await using (var db = h.CreateContext())
        {
            db.WallRefreshes.Add(new WallRefresh { WallId = h.WallId, CreatedByUserId = h.Owner.Id });
            await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        }

        Assert.Equal(first.Id, (await s.Service.BeginAsync(h.WallId)).Id);
    }

    [Fact]
    public async Task Upload_ChecksTheCallerBeforeReadingTheBody()
    {
        var refreshes = Substitute.For<IWallRefreshService>();
        refreshes.EnsureCanUploadAsync(Arg.Any<Guid>(), Arg.Any<bool>()).Returns(Task.FromException(new UnauthorizedAccessException()));
        var http = new DefaultHttpContext();
        http.Request.Body = new UnreadableStream();

        var result = await WallRefreshUploadEndpoint.HandleAsync(
            Guid.NewGuid(), "IMG_1.jpg", http, refreshes, new WallCapturePipelineOptions(), CancellationToken.None);

        Assert.IsType<ForbidHttpResult>(result);
        await refreshes.DidNotReceiveWithAnyArgs().AddPhotoAsync(default, default, default!, default);
    }

    [Fact]
    public async Task Upload_AnOversizeVideo_IsRefusedWithItsSize()
    {
        var options = new WallCapturePipelineOptions();
        var http = new DefaultHttpContext();
        http.Request.ContentLength = options.MaxVideoBytes + 1;
        http.Request.Body = new UnreadableStream();

        var result = await WallRefreshUploadEndpoint.HandleAsync(
            Guid.NewGuid(), "walk.mov", http, Substitute.For<IWallRefreshService>(), options, CancellationToken.None);

        var file = Assert.IsType<Ok<RefreshFile>>(result).Value!;
        Assert.Contains("larger than", file.Problem);
        Assert.Contains("video", file.Problem);
    }
}
