// <copyright file="HoldColorQuickApplyTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Tests.Refresh;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests.HoldColors;

/// <summary>
/// The quick apply of "Update panels + 3D" (the path the API takes): the holds it adds get the colour the new panel
/// photo shows, without anybody opening the review.
/// </summary>
public class HoldColorQuickApplyTests
{
    [Fact]
    public async Task TheNewHoldsOfAQuickApply_GetAProposal()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync(holds: 0);

        // Both dropped photos show a red hold; the second differs a little so it is not the same file.
        s.Photo = i => HoldColorTestPhoto.BuildJpeg(
            (HoldColorScenario.RedSpot.X, HoldColorScenario.RedSpot.Y, 0.06, HoldColorTestPhoto.Red),
            (0.9, 0.9, 0.01 + (0.005 * i), HoldColorTestPhoto.Green));
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(new List<DetectedHold>
            {
                new(HoldColorScenario.RedSpot.X, HoldColorScenario.RedSpot.Y, 0.07, null, 0.9),
                new(HoldColorScenario.WallSpot.X, HoldColorScenario.WallSpot.Y, 0.05, null, 0.9),
            }));

        var view = await s.PrepareAsync();
        Assert.Equal(WallRefreshStatus.ReadyToApply, view.Status);
        await s.Service.ApplyAsync(view.Id);
        await s.RunQueuedAsync();

        await using var db = h.CreateContext();
        Assert.Equal(1, (await db.Walls.FirstAsync(w => w.Id == h.WallId)).CurrentGeneration);
        var live = await db.Holds.AsNoTracking().Where(x => x.WallId == h.WallId && x.Generation == 1).OrderBy(x => x.X).ToListAsync();
        Assert.Equal(2, live.Count);
        Assert.Equal(("red", true), (live[0].Color, live[0].HasProposedColor));
        Assert.Null(live[1].Color);
    }
}
