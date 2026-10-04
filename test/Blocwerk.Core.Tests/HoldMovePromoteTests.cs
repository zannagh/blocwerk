// <copyright file="HoldMovePromoteTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A hold that physically moved between two panel generations, end to end through the real promote: within the cutoff it
/// stays on its boulders (which are marked for review), beyond it it comes off them (they are marked for revision, never
/// deleted), the lineage link records the measurement, and the "Then" view still draws the old position.
/// </summary>
public class HoldMovePromoteTests
{
    [Fact]
    public async Task MovedSixCm_StaysOnTheBoulder_AndTheBoulderNeedsReview()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1060);

        await s.Service.PromoteAsync(s.WallId, Confirm(s), s.SessionId);

        await using var db = h.CreateContext();
        var boulder = await db.Boulders.SingleAsync(b => b.Id == s.BoulderId);
        Assert.True(boulder.NeedsReview);
        Assert.False(boulder.IsHistoric);
        Assert.Equal(3, boulder.Generation);
        var holds = await db.BoulderHolds.Where(b => b.BoulderId == s.BoulderId).Select(b => b.HoldId).ToListAsync();
        Assert.Equal(new[] { s.NewMover, s.NewStayer }.Order(), holds.Order());

        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover);
        Assert.Equal(HoldMoveOutcome.Kept, link.MoveOutcome);
        Assert.Equal(HoldMoveSource.ThreeD, link.MoveSource);
        Assert.Equal(60, link.MoveDistanceMm!.Value, 1);
        var stayer = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldStayer);
        Assert.Equal(HoldMoveOutcome.Stayed, stayer.MoveOutcome);

        var moves = await h.BoulderService.GetBoulderMovesAsync(s.BoulderId);
        var move = Assert.Single(moves);
        Assert.Equal("moved 6 cm, kept", move.Text);
    }

    [Fact]
    public async Task MovedThirtyFourCm_IsRemovedFromTheBoulder_WhichIsFlaggedNotDeleted_AndThenStillShowsIt()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1340);

        await s.Service.PromoteAsync(s.WallId, Confirm(s), s.SessionId);

        await using var db = h.CreateContext();
        var boulder = await db.Boulders.SingleAsync(b => b.Id == s.BoulderId);
        Assert.True(boulder.NeedsReview);
        Assert.False(boulder.IsHistoric);
        Assert.False(boulder.IsArchived);
        var membership = await db.BoulderHolds.SingleAsync(b => b.BoulderId == s.BoulderId);
        Assert.Equal(s.NewStayer, membership.HoldId);

        // The new hold exists and is linked; the old row is history.
        Assert.True(await db.Holds.AnyAsync(x => x.Id == s.NewMover && x.Generation == 3));
        Assert.True(await db.Holds.AnyAsync(x => x.Id == s.OldMover && x.Generation == 2));
        var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover);
        Assert.Equal(HoldMoveOutcome.Removed, link.MoveOutcome);

        var move = Assert.Single(await h.BoulderService.GetBoulderMovesAsync(s.BoulderId));
        Assert.Equal("moved 34 cm, removed from this boulder", move.Text);
        Assert.Equal(HoldType.Start, move.Type);

        var then = await h.BoulderService.GetBoulderHoldsAtGenerationAsync(s.BoulderId, 2);
        Assert.NotNull(then);
        Assert.Equal(new[] { s.OldMover, s.OldStayer }.Order(), then!.Select(t => t.HoldId).Order());
        Assert.Equal(HoldType.Start, then.Single(t => t.HoldId == s.OldMover).Type);
    }

    [Fact]
    public async Task WithinNoise_NothingIsFlagged()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1010);

        await s.Service.PromoteAsync(s.WallId, Confirm(s), s.SessionId);

        await using var db = h.CreateContext();
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Empty(await db.BoulderHoldMoves.ToListAsync());
    }

    [Fact]
    public async Task ThePlanIsPartOfWhatWasConfirmed_APromoteOfADifferentPlanIsRefused()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1340);
        var confirmation = Confirm(s);
        var plan = await s.Service.PreviewHoldMovesAsync(s.WallId, confirmation);
        var removed = Assert.Single(plan.Moves, m => m.Outcome == HoldMoveOutcome.Removed);
        Assert.Equal(s.OldMover, removed.OldHoldId);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            s.Service.PromoteAsync(s.WallId, confirmation with { ExpectedMovesVersion = "stale" }, s.SessionId));
        await s.Service.PromoteAsync(s.WallId, confirmation with { ExpectedMovesVersion = plan.Version }, s.SessionId);

        await using var db = h.CreateContext();
        Assert.Single(await db.BoulderHoldMoves.ToListAsync());
    }

    [Fact]
    public async Task TwoD_FallbackFlagsAHoldTheWarpPutElsewhere()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1000, placeStaged: false);
        var warp = new Dictionary<Guid, HoldPositionNorm>
        {
            [s.OldStayer] = new(0.2, 0.5),
            [s.OldMover] = new(0.5, 0.5),
        };

        var plan = await s.Service.PreviewHoldMovesAsync(s.WallId, Confirm(s) with { CarriedWarpPositions = warp });

        // No placements on the new holds, so the scale cannot be derived from the stayers and nothing is guessed.
        Assert.Empty(plan.Moves);
    }

    internal static BigUpdateConfirmation Confirm(Scenario s) =>
        new(
            [new CarryoverDecision(s.OldMover, CarryKind.Carried, s.NewMover), new CarryoverDecision(s.OldStayer, CarryKind.Carried, s.NewStayer)],
            [], [], []);

    private static Hold Placed(Guid wallId, Guid panelId, int gen, double a, double b, double x, double y) => new()
    {
        WallId = wallId, WallPanelId = panelId, Generation = gen, X = x, Y = y, Radius = 0.02, FacetId = "0", PlaneAMm = a, PlaneBMm = b,
        IsAutoDetected = gen == 3,
    };

    internal static async Task<Scenario> SeedAsync(WallTestHarness h, double movedToA, bool placeStaged = true)
    {
        await using var db = h.CreateContext();
        db.Users.Add(h.Owner);
        var wall = new Wall
        {
            Name = "Moves", OwnerId = h.Owner.Id, CurrentGeneration = 2, Photo = [1, 2, 3], PhotoContentType = "image/jpeg", UsesMultipleImages = true,
        };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = h.Owner.Id, Role = WallRole.Admin });
        var live = new WallPanel { WallId = wall.Id, Col = 0, Row = 0, Photo = [1], PhotoContentType = "image/jpeg", Generation = 2 };
        var staged = new WallPanel { WallId = wall.Id, Col = 0, Row = 0, StagedPhoto = [7], StagedPhotoContentType = "image/jpeg", Generation = 3 };
        db.WallPanels.AddRange(live, staged);

        var oldMover = Placed(wall.Id, live.Id, 2, 1000, 1000, 0.5, 0.5);
        var oldStayer = Placed(wall.Id, live.Id, 2, 2000, 1000, 0.2, 0.5);
        var newMover = Placed(wall.Id, staged.Id, 3, movedToA, 1000, 0.5, 0.5);
        var newStayer = Placed(wall.Id, staged.Id, 3, 2000, 1000, 0.2, 0.5);
        if (!placeStaged)
        {
            foreach (var n in new[] { newMover, newStayer })
            {
                (n.FacetId, n.PlaneAMm, n.PlaneBMm) = (null, null, null);
            }
        }

        db.Holds.AddRange(oldMover, oldStayer, newMover, newStayer);
        var boulder = new Boulder { WallId = wall.Id, Name = "Mover route", CreatedByUserId = h.Owner.Id, Generation = 2 };
        db.Boulders.Add(boulder);
        db.BoulderHolds.AddRange(
            new BoulderHold { BoulderId = boulder.Id, HoldId = oldMover.Id, Type = HoldType.Start },
            new BoulderHold { BoulderId = boulder.Id, HoldId = oldStayer.Id, Type = HoldType.Top });
        var session = WallUpdateSessions.Open(db, wall.Id, 3, h.Owner.Id);
        await db.SaveChangesAsync();

        var service = new WallBigUpdateService(
            h.DbContextFactory, h.CurrentUser, h.HoldDetection, new PositionHoldMatcher(), NullLogger<WallBigUpdateService>.Instance);
        return new Scenario(wall.Id, session.Id, boulder.Id, oldMover.Id, oldStayer.Id, newMover.Id, newStayer.Id, service);
    }

    internal sealed record Scenario(
        Guid WallId, Guid SessionId, Guid BoulderId, Guid OldMover, Guid OldStayer, Guid NewMover, Guid NewStayer, WallBigUpdateService Service);
}
