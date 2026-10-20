// <copyright file="WallRefreshReOutlineApplyTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using static Blocwerk.Core.Tests.HoldLookFixture;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// "Update panels + 3D" in quick mode (the page's and the API's path) on a wall whose outlined hold was re-outlined: the
/// detection is a larger footprint whose centre sits 4 cm off, which only the old hold's WARPED OUTLINE explains. The confirm
/// screen's move plan and the promote's must be derived from the same payload (outline included), or Apply is refused with
/// "The moved holds changed since they were confirmed" every time (experimental, 2026-10-20).
/// </summary>
public class WallRefreshReOutlineApplyTests
{
    private const int PhotoWidth = 800;
    private const int PhotoHeight = 600;

    [Fact]
    public async Task AReOutlinedCarriedHold_IsNoMoveOnTheSummary_AndApplyPromotesIt()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        var (target, boulder) = await ArrangeAsync(h, s);

        var view = await s.PrepareAsync();

        Assert.Equal(WallRefreshStatus.ReadyToApply, view.Status);
        Assert.Null(view.Summary!.MovedHolds);

        await s.Service.ApplyAsync(view.Id);
        await s.RunQueuedAsync();

        var applied = await s.CurrentAsync();
        Assert.Null(applied.Error);
        Assert.Equal(WallRefreshStatus.Done, applied.Status);
        await using var db = h.CreateContext();
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == target);
        Assert.Equal((HoldMoveOutcome.Stayed, HoldMoveSource.ReOutline), (link.MoveOutcome, link.MoveSource));
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == boulder)).NeedsReview);
    }

    /// <summary>
    /// The anchors of <see cref="HoldLookFixture"/> placed on one facet (5 mm per pixel of the 800 x 600 photo, so the photo
    /// measure has a scale), found again where they were; the target an outlined 16 x 12 px hold a boulder starts on, detected
    /// again as a 24 px circle 8 px (40 mm) to the right.
    /// </summary>
    private static async Task<(Guid TargetId, Guid BoulderId)> ArrangeAsync(WallTestHarness h, RefreshScenario s)
    {
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        s.Matcher = new SameFrameShapeMatcher();
        s.Photo = i => CaptureScenario.FlatJpeg(40 + i, PhotoWidth, PhotoHeight);
        var detected = Anchors.Select(a => new DetectedHold(a.X, a.Y, 0.03, null, 0.9))
            .Append(new DetectedHold(TargetX + 0.01, TargetY, 0.03, null, 0.9))
            .ToList();
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(detected.ToList()));

        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        var panel = await db.WallPanels.SingleAsync(p => p.WallId == h.WallId && p.Photo != null);
        wall.Photo = panel.Photo = CaptureScenario.FlatJpeg(1, PhotoWidth, PhotoHeight);
        Hold Old(double x, double y) => new()
        {
            WallId = wall.Id, WallPanelId = panel.Id, X = x, Y = y, Radius = 0.03, IsAutoDetected = true, Generation = wall.CurrentGeneration,
            FacetId = "0", PlaneAMm = x * PhotoWidth * 5, PlaneBMm = y * PhotoHeight * 5,
        };
        db.Holds.AddRange(Anchors.Select(a => Old(a.X, a.Y)));
        var target = Old(TargetX, TargetY);
        (target.Name, target.Radius) = ("Outlined crimp", 0.0125);
        target.ShapePoints =
        [
            new() { Dx = -0.01, Dy = -0.01 }, new() { Dx = 0.01, Dy = -0.01 }, new() { Dx = 0.01, Dy = 0.01 }, new() { Dx = -0.01, Dy = 0.01 },
        ];
        db.Holds.Add(target);
        var boulder = new Boulder { WallId = wall.Id, Name = "Crimp line", CreatedByUserId = h.Owner.Id, Generation = wall.CurrentGeneration };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = target.Id });
        await db.SaveChangesAsync();
        return (target.Id, boulder.Id);
    }
}
