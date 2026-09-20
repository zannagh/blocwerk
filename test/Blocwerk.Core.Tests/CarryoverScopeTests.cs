// <copyright file="CarryoverScopeTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Cover for <see cref="CarryoverScope"/>: the guard that stops a carry verdict recorded about a hold
/// no review surface can display from taking effect unseen.
/// <para>
/// The carryover review draws the centre panel only, so a build that drew the WHOLE old generation on
/// that photo let the user record verdicts against phantom circles belonging to co-updated NEIGHBOUR
/// panels. Those verdicts are persisted <c>WallUpdateHoldDecision</c> rows: scoping the drawing does not
/// remove them, and a resume that lands straight on Neighbours / Touch-up / Confirm reads them back and
/// promotes them untouched. A stale <see cref="CarryKind.Removed"/> among them freezes real boulders —
/// <c>FreezeRemovedBouldersAsync</c> runs BEFORE the promote's old-hold lookup guard — with nothing on
/// screen drawing, counting or offering to revert it.
/// </para>
/// </summary>
public class CarryoverScopeTests
{
    // The headline invariant: a stale Removed on a NON-DISPLAYED panel is reset to the matcher default
    // and reported, and the reconciled set promotes as a plain carry — the neighbour's boulder survives.
    [Fact]
    public async Task StaleRemovedOnNonDisplayedPanel_IsResetAndReported_AndPromotesAsACarry()
    {
        using var h = new WallTestHarness();
        var w = await SeedWallAsync(h);
        await StageCentrePlusNeighbourAsync(h, w.WallId);

        var session = await BuildService(h).ResumeAsync(w.WallId);
        var stale = new CarryoverDecision(w.NeighbourHoldId, CarryKind.Removed, null);

        var reconciled = CarryoverScope.Reconcile(session, [stale]);

        // Reported, not dropped: the caller has what it needs to tell the user how many were reset.
        Assert.Equal([stale], reconciled.Reset);

        // And replaced by the matcher default — carried, with the matcher's SAME-panel twin when it
        // found one, which is exactly what the promote's undecided-reconcile would have done anyway.
        var safe = Assert.Single(reconciled.Decisions);
        Assert.Equal(w.NeighbourHoldId, safe.OldHoldId);
        Assert.Equal(CarryKind.Carried, safe.Kind);

        // It reaches the promote as a carry: the boulder on that neighbour hold is NOT frozen, and the
        // hold has a successor at the new generation.
        await BuildService(h).PromoteAsync(w.WallId, Confirm(reconciled.Decisions));

        await using var db = h.CreateContext();
        var boulder = await db.Boulders.SingleAsync(b => b.Id == w.NeighbourBoulderId);
        Assert.False(boulder.IsHistoric);
        Assert.Equal(3, boulder.Generation);
        Assert.True(await db.HoldGenerationLinks.AnyAsync(l => l.OldHoldId == w.NeighbourHoldId));
    }

    // NON-VACUOUS: the very same stale row, promoted WITHOUT the reconcile, retires the boulder to
    // history — the silent freeze this guard exists to prevent.
    [Fact]
    public async Task StaleRemovedOnNonDisplayedPanel_WithoutTheReconcile_FreezesTheBoulder()
    {
        using var h = new WallTestHarness();
        var w = await SeedWallAsync(h);
        await StageCentrePlusNeighbourAsync(h, w.WallId);

        await BuildService(h).PromoteAsync(
            w.WallId, Confirm([new CarryoverDecision(w.NeighbourHoldId, CarryKind.Removed, null)]));

        await using var db = h.CreateContext();
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == w.NeighbourBoulderId)).IsHistoric);
    }

    // A verdict about a hold on the DISPLAYED panel is the user's to make: it is never touched, however
    // destructive, because they can see it, count it and revert it.
    [Fact]
    public async Task RemovedOnTheDisplayedPanel_IsLeftExactlyAsRecorded()
    {
        using var h = new WallTestHarness();
        var w = await SeedWallAsync(h);
        await StageCentrePlusNeighbourAsync(h, w.WallId);

        var session = await BuildService(h).ResumeAsync(w.WallId);
        var removed = new CarryoverDecision(w.CentreHoldIds[0], CarryKind.Removed, null);

        var reconciled = CarryoverScope.Reconcile(session, [removed]);

        Assert.Empty(reconciled.Reset);
        Assert.Equal([removed], reconciled.Decisions);
    }

    // An out-of-scope verdict that ALREADY is the matcher default is not a reset and must not be
    // reported — otherwise every resume of a healthy session would cry wolf at the user.
    [Fact]
    public async Task OutOfScopeCarryMatchingTheMatcherDefault_IsNotReportedAsAReset()
    {
        using var h = new WallTestHarness();
        var w = await SeedWallAsync(h);
        await StageCentrePlusNeighbourAsync(h, w.WallId);

        var session = await BuildService(h).ResumeAsync(w.WallId);
        var twin = session.Carryover.SingleOrDefault(p => p.OldHoldId == w.NeighbourHoldId)?.NewHoldId;
        var asMatched = new CarryoverDecision(w.NeighbourHoldId, CarryKind.Carried, twin);

        var reconciled = CarryoverScope.Reconcile(session, [asMatched]);

        Assert.Empty(reconciled.Reset);
        Assert.Equal([asMatched], reconciled.Decisions);
    }

    // With no carried panels (the pre-match staged session) the scope is unknowable, so nothing is
    // touched: guessing at a scope there could reset a verdict the user CAN see.
    [Fact]
    public void UnknownScope_LeavesEveryDecisionAlone()
    {
        var session = new BigUpdateSession(Guid.NewGuid(), Guid.NewGuid(), [], [], [], []);
        var decision = new CarryoverDecision(Guid.NewGuid(), CarryKind.Removed, null);

        var reconciled = CarryoverScope.Reconcile(session, [decision]);

        Assert.Empty(reconciled.Reset);
        Assert.Equal([decision], reconciled.Decisions);
    }

    private static BigUpdateConfirmation Confirm(IReadOnlyList<CarryoverDecision> carryover) =>
        new([.. carryover], [], [], []);

    private static WallBigUpdateService BuildService(WallTestHarness h) =>
        new(
            h.DbContextFactory,
            h.CurrentUser,
            h.HoldDetection,
            new IndexAlignedMatcher(),
            NullLogger<WallBigUpdateService>.Instance);

    // Live gen-2 wall: centre (0,0) with 33 real holds, a neighbour (1,0) with one hold that a live
    // boulder uses. The centre population is comfortably over the mat filter's minimum so nothing is
    // dropped for being an unrepresentative sample.
    private static async Task<ScopeWall> SeedWallAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);

        var wall = new Wall
        {
            Name = "Attic", OwnerId = h.Owner.Id, CurrentGeneration = 2,
            Photo = [1, 2, 3], PhotoContentType = "image/jpeg", UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });

        var centre = new WallPanel { WallId = wall.Id, Col = 0, Row = 0, Photo = [1], PhotoContentType = "image/jpeg", Generation = 2 };
        var neighbour = new WallPanel { WallId = wall.Id, Col = 1, Row = 0, Photo = [2], PhotoContentType = "image/jpeg", Generation = 2 };
        db.WallPanels.AddRange(centre, neighbour);

        var centreHolds = new List<Hold>();
        for (var i = 0; i < 33; i++)
        {
            centreHolds.Add(new Hold
            {
                WallId = wall.Id, WallPanelId = centre.Id,
                X = 0.10 + (0.025 * i), Y = 0.10 + (0.02 * i),
                Radius = 0.016 + (0.001 * (i % 5)), Generation = 2,
            });
        }

        var neighbourHold = new Hold
        {
            WallId = wall.Id, WallPanelId = neighbour.Id, X = 0.55, Y = 0.40, Radius = 0.02, Generation = 2,
        };
        db.Holds.AddRange(centreHolds);
        db.Holds.Add(neighbourHold);

        // The boulder that a stale phantom verdict would silently retire to history.
        var boulder = new Boulder { WallId = wall.Id, Name = "Neighbour", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(boulder);
        db.BoulderHolds.Add(new BoulderHold { BoulderId = boulder.Id, HoldId = neighbourHold.Id });

        await db.SaveChangesAsync();
        return new ScopeWall(
            wall.Id, centreHolds.Select(x => x.Id).ToList(), neighbourHold.Id, boulder.Id);
    }

    // Stages a centre + (1,0) neighbour update at gen 3, each panel with one staged detection, exactly
    // as StageAsync would leave the database.
    private static async Task StageCentrePlusNeighbourAsync(WallTestHarness h, Guid wallId)
    {
        await using var db = h.CreateContext();

        var centrePanel = new WallPanel
        {
            WallId = wallId, Col = 0, Row = 0, Photo = null,
            StagedPhoto = [7], StagedPhotoContentType = "image/jpeg", Generation = 3,
        };
        var neighbourPanel = new WallPanel
        {
            WallId = wallId, Col = 1, Row = 0, Photo = null,
            StagedPhoto = [8], StagedPhotoContentType = "image/jpeg", Generation = 3,
        };
        db.WallPanels.AddRange(centrePanel, neighbourPanel);

        db.Holds.AddRange(
            new Hold
            {
                WallId = wallId, WallPanelId = centrePanel.Id, X = 0.11, Y = 0.11, Radius = 0.02,
                Generation = 3, IsAutoDetected = true, NeedsReview = true,
            },
            new Hold
            {
                WallId = wallId, WallPanelId = neighbourPanel.Id, X = 0.56, Y = 0.41, Radius = 0.02,
                Generation = 3, IsAutoDetected = true, NeedsReview = true,
            });

        await db.SaveChangesAsync();
    }

    /// <summary>
    /// A deterministic stand-in for the OpenCV matcher: proposes left[i] ↔ right[i] for the overlapping
    /// prefix and leaves the rest unmatched, so the session build runs end to end without native OpenCV.
    /// </summary>
    private sealed class IndexAlignedMatcher : IHoldOverlapMatcher
    {
        public HoldOverlapResult Match(
            byte[] leftImage,
            IReadOnlyList<MatcherHold> leftHolds,
            byte[] rightImage,
            IReadOnlyList<MatcherHold> rightHolds,
            HoldOverlapDirection direction,
            ILogger? diag = null)
        {
            var n = Math.Min(leftHolds.Count, rightHolds.Count);
            var proposals = new List<HoldOverlapProposal>();
            for (var i = 0; i < n; i++)
            {
                proposals.Add(new HoldOverlapProposal(leftHolds[i].Id, rightHolds[i].Id, 0.9, false, 1.0, null));
            }

            return new HoldOverlapResult(
                proposals,
                leftHolds.Skip(n).Select(hold => hold.Id).ToList(),
                rightHolds.Skip(n).Select(hold => hold.Id).ToList());
        }
    }

    private sealed record ScopeWall(
        Guid WallId,
        List<Guid> CentreHoldIds,
        Guid NeighbourHoldId,
        Guid NeighbourBoulderId);
}
