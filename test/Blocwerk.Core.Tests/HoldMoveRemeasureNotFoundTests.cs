// <copyright file="HoldMoveRemeasureNotFoundTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.HoldMoves;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The 3D re-measurement knows moves only. A boulder that was told "not found again" or "another hold is here now" keeps
/// that record and its review mark, whatever the hold's copy measures: neither a later "it moved" nor a later "it stayed"
/// is about what the boulder was told.
/// </summary>
public class HoldMoveRemeasureNotFoundTests
{
    [Theory]
    [InlineData(HoldMoveOutcome.NotFound)]
    [InlineData(HoldMoveOutcome.Replaced)]
    public async Task ARecordOfAHoldNotFoundAgain_IsNeitherEscalatedNorSettled_OnLaterRuns(HoldMoveOutcome outcome)
    {
        using var h = new WallTestHarness();
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1000, placeStaged: false);
        await s.Service.PromoteAsync(s.WallId, HoldMovePromoteTests.Confirm(s), s.SessionId);
        await using (var db = h.CreateContext())
        {
            db.BoulderHoldMoves.Add(new BoulderHoldMove
            {
                WallId = s.WallId, BoulderId = s.BoulderId, OldHoldId = s.OldMover, NewHoldId = s.NewMover, Outcome = outcome,
                Source = HoldMoveSource.TwoD, FromGeneration = 2, ToGeneration = 3,
            });
            (await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview = true;
            await db.SaveChangesAsync();

            // First run: the placement puts the copy 34 cm away. That is not what the boulder was told about.
            await HoldMovePromoteTests.PlaceNewAsync(db, s, 1340);
        }

        await using (var db = h.CreateContext())
        {
            Assert.Equal(0, await HoldMoveRemeasurer.RunAsync(db, s.WallId, HoldMovePromoteTests.Loose, NullLogger.Instance));
            Assert.Equal(HoldMoveOutcome.Possible, (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover)).MoveOutcome);

            // Second run at the same generation: now it measures as stayed, which would settle a move.
            await HoldMovePromoteTests.PlaceNewAsync(db, s, 1002);
        }

        await using (var db = h.CreateContext())
        {
            await HoldMoveRemeasurer.RunAsync(db, s.WallId, HoldMovePromoteTests.Loose, NullLogger.Instance);
        }

        await using var check = h.CreateContext();
        Assert.True((await check.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        var row = await check.BoulderHoldMoves.SingleAsync();
        Assert.Equal((outcome, null, null), (row.Outcome, row.RemeasuredOutcome, row.RemeasuredDistanceMm));
        Assert.DoesNotContain("Measured again", Assert.Single(await h.BoulderService.GetBoulderMovesAsync(s.BoulderId)).Text);
    }

    [Fact]
    public async Task OnTheBoulder_RemovedComesFirst_ThenReplaced_ThenNotFound_ThenMoves()
    {
        using var h = new WallTestHarness();
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1000, placeStaged: false);
        await s.Service.PromoteAsync(s.WallId, HoldMovePromoteTests.Confirm(s), s.SessionId);
        await using (var db = h.CreateContext())
        {
            BoulderHoldMove Row(HoldMoveOutcome outcome, double mm) => new()
            {
                WallId = s.WallId, BoulderId = s.BoulderId, OldHoldId = s.OldMover, NewHoldId = s.NewMover, Outcome = outcome, DistanceMm = mm,
                Source = HoldMoveSource.TwoD, FromGeneration = 2, ToGeneration = 3,
            };
            db.BoulderHoldMoves.AddRange(
                Row(HoldMoveOutcome.Kept, 60), Row(HoldMoveOutcome.NotFound, 0), Row(HoldMoveOutcome.Possible, 200), Row(HoldMoveOutcome.Removed, 300),
                Row(HoldMoveOutcome.Replaced, 0));
            (await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview = true;
            await db.SaveChangesAsync();
        }

        var shown = await h.BoulderService.GetBoulderMovesAsync(s.BoulderId);

        Assert.Equal(
            [HoldMoveOutcome.Removed, HoldMoveOutcome.Replaced, HoldMoveOutcome.NotFound, HoldMoveOutcome.Possible, HoldMoveOutcome.Kept],
            shown.Select(m => m.Outcome));
    }
}
