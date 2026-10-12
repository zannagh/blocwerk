// <copyright file="LookOverRefreshTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Tests.Refresh;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using static Blocwerk.Core.Tests.HoldLookFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// "Update panels + 3D" applied with nobody answering its cards, which is what the API does and what the page does when
/// Apply is pressed straight away: the finished run says how much is left for the look-over, and the wall is flagged.
/// </summary>
public class LookOverRefreshTests
{
    [Fact]
    public async Task ApplyWithAnOpenCard_FlagsTheWall_KeepsTheFinding_AndTheFinishedRunSaysSo()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        var probe = new ScriptedPresenceProbe();
        var (target, boulder) = await ArrangeAsync(h, s, probe);
        probe.Set(TargetX, TargetY, 0.1);
        var view = await s.PrepareAsync();
        Assert.Equal(1, view.Summary!.ChecksOpen);
        Assert.Null(view.LookOver);

        await s.Service.ApplyAsync(view.Id);
        await s.RunQueuedAsync();

        var done = await s.CurrentAsync();
        Assert.Equal(WallRefreshStatus.Done, done.Status);
        Assert.Equal(1, done.LookOver!.OpenFindings);
        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        Assert.Equal((done.LookOver.PendingSince, (int?)wall.CurrentGeneration), (wall.LookOverPendingSince!.Value, wall.LookOverGeneration));
        var finding = await db.WallLookOverFindings.SingleAsync();
        var kept = (await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == boulder)).HoldId;
        Assert.Equal(
            (LookOverFindingKind.ReplacedByOther, (Guid?)target, (Guid?)kept, true, 1),
            (finding.Kind, finding.OldHoldId, finding.HoldId, finding.FlaggedHold, finding.BouldersAtApply));
        Assert.NotNull(finding.OtherHoldId);

        // "Update panels + 3D" marks the copy it kept, like the step-by-step update does.
        Assert.Equal((true, (int?)wall.CurrentGeneration), (finding.HoldKept, wall.KeptHoldsGeneration));
        Assert.Equal([kept], await db.Holds.Where(x => x.KeptUnconfirmedGeneration == wall.CurrentGeneration).Select(x => x.Id).ToListAsync());
    }

    [Fact]
    public async Task ApplyWithNothingUnsure_LeavesTheWallUnflagged()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await ArrangeAsync(h, s, new ScriptedPresenceProbe());
        var view = await s.PrepareAsync();

        await s.Service.ApplyAsync(view.Id);
        await s.RunQueuedAsync();

        Assert.Null((await s.CurrentAsync()).LookOver);
        await using var db = h.CreateContext();
        Assert.Null((await db.Walls.SingleAsync(w => w.Id == h.WallId)).LookOverPendingSince);
        Assert.Empty(await db.WallLookOverFindings.ToListAsync());
    }

    /// <summary>The wall of <c>WallRefreshNotFoundTests</c>: the anchors and a named target a boulder uses, all detected again.</summary>
    private static async Task<(Guid TargetId, Guid BoulderId)> ArrangeAsync(WallTestHarness h, RefreshScenario s, ScriptedPresenceProbe probe)
    {
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        s.Matcher = new SameFrameHoldMatcher();
        s.PresenceProbe = probe;
        var detected = Anchors.Append((X: TargetX, Y: TargetY)).Select(a => new DetectedHold(a.X, a.Y, 0.03, null, 0.9)).ToList();
        h.HoldDetection.DetectHoldsAsync(Arg.Any<byte[]>(), Arg.Any<HoldDetectionParameters?>())
            .Returns(_ => Task.FromResult(detected.ToList()));

        await using var db = h.CreateContext();
        var wall = await db.Walls.SingleAsync(w => w.Id == h.WallId);
        var panel = await db.WallPanels.SingleAsync(p => p.WallId == h.WallId && p.Photo != null);
        wall.Photo = panel.Photo = CaptureScenario.TinyJpeg(1);
        Hold Old(double x, double y) => new()
        {
            WallId = wall.Id, WallPanelId = panel.Id, X = x, Y = y, Radius = 0.03, IsAutoDetected = true, Generation = wall.CurrentGeneration,
        };
        db.Holds.AddRange(Anchors.Select(a => Old(a.X, a.Y)));
        var target = Old(TargetX, TargetY);
        target.Name = "Yellow crimp";
        db.Holds.Add(target);
        var boulder = new Boulder { WallId = wall.Id, Name = "Crimp line", CreatedByUserId = h.Owner.Id, Generation = wall.CurrentGeneration };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = target.Id });
        await db.SaveChangesAsync();
        return (target.Id, boulder.Id);
    }
}
