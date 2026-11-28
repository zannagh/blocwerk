// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>/api/v1/beta-videos/{id}</c> for the companion app: the web's own byte, poster and HLS handlers, behind the same
/// gate (a member of the clip's wall), so an outsider and a clip still being normalized are a 404.
/// </summary>
public sealed class DeviceBetaVideoPlaybackControllerTests
{
    private static readonly byte[] Clip = [0x00, 0x00, 0x00, 0x18, 0x66, 0x74, 0x79, 0x70];

    [Fact]
    public async Task AMember_PlaysAReadyClip_NotAPendingOne_AndAnOutsiderNothing()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var boulder = await VisitFixture.SeedBoulderAsync(h, h.WallId);
        var clip = await h.BetaVideoService.AddVideoAsync(boulder, Clip, "video/mp4", null, "beta.mp4");

        Assert.IsType<NotFound>(await Controller(h).Video(clip.Id)); // still Pending
        await using (var db = h.CreateContext())
        {
            await db.BetaVideos.Where(v => v.Id == clip.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.EncodingStatus, BetaVideoEncodingStatus.Ready));
        }

        Assert.IsNotType<NotFound>(await Controller(h).Video(clip.Id));
        Assert.IsType<NotFound>(await Controller(h).Hls(clip.Id, "master.m3u8", h.BetaStorage)); // no ladder

        await using (var db = h.CreateContext())
        {
            var outsider = new User { Identifier = "outsider@test", DisplayName = "Outsider" };
            db.Users.Add(outsider);
            await db.SaveChangesAsync();
            h.ActingUser = outsider;
        }

        Assert.IsType<NotFound>(await Controller(h).Video(clip.Id));
        Assert.IsType<NotFound>(await Controller(h).Thumbnail(clip.Id));
    }

    private static DeviceBetaVideoPlaybackController Controller(WallTestHarness h) =>
        new(h.BetaVideoService)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = DeviceKeyTestPrincipals.Device() },
            },
        };
}
