// <copyright file="ProtrudingHoldMoveTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry.View3D;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.HoldMovePromoteTests;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A hold standing far out of the wall appears shifted between two photos without having moved: its 6 cm "move" is within its
/// tolerance and is not suspected, while a person who says it moved is still believed.
/// </summary>
public class ProtrudingHoldMoveTests
{
    [Fact]
    public async Task ASixCmShiftOfAHoldStanding9cmOut_NobodyConfirmed_IsNotSuspected()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1060);
        await StandOutAsync(h, s.OldMover, apexMm: 90);

        var plan = await s.Service.PreviewHoldMovesAsync(s.WallId, Confirm(s));
        await s.Service.PromoteAsync(s.WallId, Confirm(s) with { ExpectedMovesVersion = plan.Version }, s.SessionId);

        Assert.Equal(HoldMoveOutcome.Stayed, plan.For(s.OldMover)!.Outcome);
        await using var db = h.CreateContext();
        Assert.False((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Equal(HoldMoveOutcome.Stayed, (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover)).MoveOutcome);
    }

    [Fact]
    public async Task TheSameShift_ConfirmedByAPerson_StillMarksTheBoulder()
    {
        using var h = new WallTestHarness();
        var s = await SeedAsync(h, movedToA: 1060);
        await StandOutAsync(h, s.OldMover, apexMm: 90);

        await s.Service.PromoteAsync(s.WallId, Confirm(s, CarryKind.Changed), s.SessionId);

        await using var db = h.CreateContext();
        Assert.True((await db.Boulders.SingleAsync(b => b.Id == s.BoulderId)).NeedsReview);
        Assert.Equal(HoldMoveOutcome.Kept, (await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover)).MoveOutcome);
    }

    private static async Task StandOutAsync(WallTestHarness h, Guid holdId, double apexMm)
    {
        await using var db = h.CreateContext();
        var hold = await db.Holds.SingleAsync(x => x.Id == holdId);
        hold.ProtrusionMm = new HoldProtrusion(HoldProtrusionSource.Splat, 500, 0, apexMm * 0.8, 0, 0, apexMm, HoldFootprint.KeyOf(hold)).ToJson();
        await db.SaveChangesAsync();
    }
}
