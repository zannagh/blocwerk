// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The hold-move columns on the lineage link and the <c>BoulderHoldMoves</c> table on a real PostgreSQL: the migration
/// SQL, the nullable enum columns and the SET NULL foreign keys when a hold of a recorded move is later deleted.
/// </summary>
[Trait("Db", "Postgres")]
public class PostgresHoldMoveTests
{
    [PostgresFact]
    public async Task APromotedMove_IsStored_AndSurvivesTheDeletionOfItsHolds()
    {
        using var h = new WallTestHarness(PostgresTestDatabase.Create());
        var s = await HoldMovePromoteTests.SeedAsync(h, movedToA: 1340);

        await s.Service.PromoteAsync(s.WallId, HoldMovePromoteTests.Confirm(s, Blocwerk.Core.Enums.CarryKind.Changed), s.SessionId);

        await using (var db = h.CreateContext())
        {
            var link = await db.HoldGenerationLinks.SingleAsync(l => l.OldHoldId == s.OldMover);
            Assert.Equal((HoldMoveOutcome.Removed, HoldMoveSource.ThreeD), (link.MoveOutcome, link.MoveSource));
            Assert.Equal(340, link.MoveDistanceMm!.Value, 1);
            var move = await db.BoulderHoldMoves.SingleAsync();
            Assert.Equal((s.OldMover, s.NewMover, HoldType.Start), (move.OldHoldId, move.NewHoldId, move.Type));
        }

        // Deleting the successor nulls that end of the record instead of failing or deleting the row.
        await using (var db = h.CreateContext())
        {
            await HoldDeletion.PrepareHoldForDeleteAsync(db, s.NewMover);
            db.Holds.Remove(await db.Holds.SingleAsync(x => x.Id == s.NewMover));
            await db.SaveChangesAsync();
        }

        await using (var db = h.CreateContext())
        {
            var move = await db.BoulderHoldMoves.SingleAsync();
            Assert.Null(move.NewHoldId);
            Assert.Equal(s.OldMover, move.OldHoldId);
        }
    }
}
