// <copyright file="WallRefreshNotFoundTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using static Blocwerk.Core.Tests.HoldLookFixture;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// "Update panels + 3D" in quick mode (the page's and the API's path) after a re-set: the summary lists, before anything
/// is applied, the holds on boulders that were not found again or were replaced; Apply marks exactly those boulders and
/// takes no hold off; the timeline's match line follows every summary that is made again.
/// </summary>
public class WallRefreshNotFoundTests
{
    [Fact]
    public async Task AHoldThatIsGone_IsListedWithItsBoulder_AndApplyMarksTheBoulder()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        var probe = new ScriptedPresenceProbe();
        var (target, boulder) = await ArrangeAsync(h, s, probe, targetDetected: false);
        probe.Set(TargetX, TargetY, 0.2);

        var view = await s.PrepareAsync();

        var summary = view.Summary!;
        Assert.Equal((Anchors.Length, 1, 0), (summary.Refound, summary.KeptInPlace, summary.ReplacedHolds));
        Assert.Equal((1, 1), (summary.NotFoundOnBoulders, summary.BouldersMarkedNotFound));
        var line = Assert.Single(summary.NotFoundHolds!);
        Assert.Equal((target, "Yellow crimp", HoldMoveOutcome.NotFound, "not found again on the new photo"), (line.OldHoldId, line.HoldName, line.Outcome, line.Text));
        Assert.Equal(["Crimp line"], line.Boulders);
        Assert.Equal($"{Anchors.Length} old holds found again, 1 kept where they were; 1 boulder will be marked for review", MatchLine(view));

        await s.Service.ApplyAsync(view.Id);
        await s.RunQueuedAsync();

        await using var db = h.CreateContext();
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == boulder)).NeedsReview);
        Assert.Single(await db.BoulderHolds.Where(bh => bh.BoulderId == boulder).ToListAsync());
        Assert.Equal(HoldMoveOutcome.NotFound, (await db.BoulderHoldMoves.SingleAsync()).Outcome);
    }

    [Fact]
    public async Task AReplacedHold_IsCountedListedAndAsked_AndAnAnswerRewritesTheMatchLine()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        var probe = new ScriptedPresenceProbe();
        var (target, _) = await ArrangeAsync(h, s, probe, targetDetected: true);
        probe.Set(TargetX, TargetY, 0.1);

        var view = await s.PrepareAsync();

        var summary = view.Summary!;
        Assert.Equal((Anchors.Length, 1, 1, 1), (summary.Refound, summary.KeptInPlace, summary.ReplacedHolds, summary.NewHolds));
        Assert.Equal((1, 1, 1), (summary.NotFoundOnBoulders, summary.BouldersMarkedNotFound, summary.ChecksOpen));
        var line = Assert.Single(summary.NotFoundHolds!);
        Assert.Equal((target, HoldMoveOutcome.Replaced, "another hold is here now"), (line.OldHoldId, line.Outcome, line.Text));
        Assert.Equal(
            $"{Anchors.Length} old holds found again, 1 kept where they were, 1 replaced by another hold; 1 boulder will be marked for review",
            MatchLine(view));
        var check = Assert.Single(await s.Service.GetChecksAsync(view.Id));
        Assert.Equal((UpdateExceptionKind.ReplacedByOther, 1, "Yellow crimp"), (check.Kind, check.Boulders, check.HoldName));

        await s.Service.DecideCheckAsync(view.Id, check.Id, UpdateExceptionAnswer.Remove);
        await s.RunQueuedAsync();

        var answered = await s.CurrentAsync();
        Assert.Equal((0, 1, 0), (answered.Summary!.KeptInPlace, answered.Summary.Removed, answered.Summary.BouldersMarkedNotFound));
        Assert.Null(answered.Summary.NotFoundHolds);
        Assert.NotEqual(summary.DecisionsVersion, answered.Summary.DecisionsVersion);

        // A removed hold is not "kept" any more (the summary has always counted it with the decided ones).
        Assert.Equal($"{Anchors.Length + 1} old holds found again, 0 kept where they were, 1 replaced by another hold", MatchLine(answered));
    }

    [Fact]
    public async Task NothingChanged_NothingIsListed_AndApplyMarksNoBoulder()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        var (_, boulder) = await ArrangeAsync(h, s, new ScriptedPresenceProbe(), targetDetected: true);

        var view = await s.PrepareAsync();

        var summary = view.Summary!;
        Assert.Equal((Anchors.Length + 1, 0, 0, 0), (summary.Refound, summary.KeptInPlace, summary.ReplacedHolds, summary.ChecksOpen));
        Assert.Equal((0, 0), (summary.NotFoundOnBoulders, summary.BouldersMarkedNotFound));
        Assert.Null(summary.NotFoundHolds);
        Assert.Equal($"{Anchors.Length + 1} old holds found again, 0 kept where they were", MatchLine(view));

        await s.Service.ApplyAsync(view.Id);
        await s.RunQueuedAsync();

        await using var db = h.CreateContext();
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == boulder)).NeedsReview);
        Assert.Empty(await db.BoulderHoldMoves.ToListAsync());
    }

    [Fact]
    public async Task ARecheck_RewritesTheMatchLineWithTheSummary()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        var probe = new ScriptedPresenceProbe();
        await ArrangeAsync(h, s, probe, targetDetected: false);
        var prepared = await s.PrepareAsync();
        Assert.Equal(0, prepared.Summary!.BouldersMarkedNotFound);
        Assert.Equal($"{Anchors.Length} old holds found again, 1 kept where they were", MatchLine(prepared));

        // What the check works out differs from the first pass: here, the hold's spot no longer shows it.
        probe.Set(TargetX, TargetY, 0.2);
        await WallRefreshRecheckTests.MakeModelReadyAsync(h, prepared.CaptureId!.Value);
        await s.CurrentAsync();
        await s.RunQueuedAsync();

        var rechecked = await s.CurrentAsync();
        Assert.Equal(1, rechecked.Summary!.BouldersMarkedNotFound);
        Assert.NotEqual(prepared.Summary.DecisionsVersion, rechecked.Summary.DecisionsVersion);
        var match = rechecked.Steps.Single(st => st.Key == RefreshTimeline.Match);
        Assert.Equal(RefreshStepState.Done, match.State);
        Assert.Equal($"{Anchors.Length} old holds found again, 1 kept where they were; 1 boulder will be marked for review", match.Detail);
    }

    private static string? MatchLine(WallRefreshView view) => view.Steps.Single(st => st.Key == RefreshTimeline.Match).Detail;

    /// <summary>
    /// The wall of <see cref="HoldLookFixture"/> under the refresh scenario: the anchors and the target on the live panel,
    /// a boulder on the target, and a detector that finds the anchors again (and the target's spot when asked to).
    /// </summary>
    private static async Task<(Guid TargetId, Guid BoulderId)> ArrangeAsync(
        WallTestHarness h, RefreshScenario s, ScriptedPresenceProbe probe, bool targetDetected)
    {
        await s.SeedMarkerWallWithEarlierCaptureAsync();
        s.Matcher = new SameFrameHoldMatcher();
        s.PresenceProbe = probe;
        var detected = (targetDetected ? Anchors.Append((X: TargetX, Y: TargetY)) : Anchors)
            .Select(a => new DetectedHold(a.X, a.Y, 0.03, null, 0.9))
            .ToList();
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
