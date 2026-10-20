// <copyright file="HoldDeletionAlreadyRemovedTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <see cref="HoldDeletion"/> with <see cref="HoldDeleteBoulderPolicy.DetachAndFlagHistoric"/> retires only the boulders
/// still on the dying hold: a membership the caller already removed in the same unit of work (moved elsewhere) is not
/// counted, and its boulder stays current.
/// </summary>
public class HoldDeletionAlreadyRemovedTests
{
    [Fact]
    public async Task AMembershipRemovedInTheSameUnitOfWork_DoesNotRetireItsBoulder()
    {
        using var h = new WallTestHarness();
        var holds = await h.SeedWallAsync(holdCount: 2);
        var (dying, other) = (holds[0].Id, holds[1].Id);
        Guid moved, staying;
        await using (var db = h.CreateContext())
        {
            var a = new Boulder { WallId = h.WallId, Name = "Moved", CreatedByUserId = h.Owner.Id };
            var b = new Boulder { WallId = h.WallId, Name = "Staying", CreatedByUserId = h.Owner.Id };
            db.Boulders.AddRange(a, b);
            db.BoulderHolds.AddRange(new BoulderHold { BoulderId = a.Id, HoldId = dying }, new BoulderHold { BoulderId = b.Id, HoldId = dying });
            await db.SaveChangesAsync();
            (moved, staying) = (a.Id, b.Id);
        }

        await using (var db = h.CreateContext())
        {
            db.BoulderHolds.Remove(await db.BoulderHolds.SingleAsync(bh => bh.BoulderId == moved && bh.HoldId == dying));
            db.BoulderHolds.Add(new BoulderHold { BoulderId = moved, HoldId = other });

            var historic = await HoldDeletion.PrepareHoldForDeleteAsync(db, dying, HoldDeleteBoulderPolicy.DetachAndFlagHistoric);
            db.Holds.Remove(await db.Holds.SingleAsync(x => x.Id == dying));
            await db.SaveChangesAsync();

            Assert.Equal(1, historic);
        }

        await using var check = h.CreateContext();
        var boulders = await check.Boulders.AsNoTracking().Include(b => b.BoulderHolds).Where(b => b.Id == moved || b.Id == staying).ToDictionaryAsync(b => b.Id);
        Assert.Equal((false, other), (boulders[moved].IsHistoric, boulders[moved].BoulderHolds.Single().HoldId));
        Assert.Equal((true, 0), (boulders[staying].IsHistoric, boulders[staying].BoulderHolds.Count));
    }
}
