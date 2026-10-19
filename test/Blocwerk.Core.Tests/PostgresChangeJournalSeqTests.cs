// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Seq is unique per batch, and the wall-update batch is resumed by several writers at once (two staged-hold edits from
/// two tabs, an edit during the promote). Each used to number its entries from "max + 1" taken when its scope opened, so
/// overlapping scopes collided on the unique index. On a real PostgreSQL server, with one connection per context.
/// </summary>
[Trait("Db", "Postgres")]
public sealed class PostgresChangeJournalSeqTests
{
    [PostgresFact]
    public async Task TwoWritersThatResumedTheBatchBeforeEitherSaved_BothSave_WithDistinctSeq()
    {
        using var pg = PostgresTestDatabase.Create();
        var (journal, interceptor, wallId) = await SetUpAsync(pg);

        // Both resume the open batch (each computes its start from the same max) before either saves.
        using var gate = new Barrier(2);
        await Task.WhenAll(
            Task.Run(() => WriteAsync(pg, journal, interceptor, wallId, gate, 0.3)),
            Task.Run(() => WriteAsync(pg, journal, interceptor, wallId, gate, 0.4)));

        await AssertDistinctSeqAsync(pg, wallId, expected: 3);
    }

    [PostgresFact]
    public async Task ManyConcurrentWriters_OnOneOpenWallUpdateBatch_NeverCollide()
    {
        using var pg = PostgresTestDatabase.Create();
        var (journal, interceptor, wallId) = await SetUpAsync(pg);
        const int writers = 8;

        using var gate = new Barrier(writers);
        await Task.WhenAll(Enumerable.Range(0, writers)
            .Select(i => Task.Run(() => WriteAsync(pg, journal, interceptor, wallId, gate, 0.1 + (i * 0.05)))));

        await AssertDistinctSeqAsync(pg, wallId, expected: writers + 1);
    }

    private static async Task<(ChangeJournal Journal, ChangeJournalInterceptor Interceptor, Guid WallId)> SetUpAsync(PostgresTestDatabase pg)
    {
        var journal = new ChangeJournal(() => pg.CreateContext());
        var interceptor = new ChangeJournalInterceptor(journal);
        var owner = new User { Identifier = "owner@test", DisplayName = "Owner" };
        var wall = new Wall { Name = "Wall", OwnerId = owner.Id };
        await using (var db = pg.CreateContext())
        {
            db.Users.Add(owner);
            db.Walls.Add(wall);
            await db.SaveChangesAsync();
        }

        // The staging write that opens the batch.
        using (journal.BeginWallUpdateBatch(wall.Id))
        {
            await using var db = pg.CreateContext(interceptor);
            db.Holds.Add(new Hold { WallId = wall.Id, X = 0.5, Y = 0.5, Radius = 0.02, Generation = 1 });
            await db.SaveChangesAsync();
        }

        return (journal, interceptor, wall.Id);
    }

    private static async Task WriteAsync(
        PostgresTestDatabase pg, ChangeJournal journal, ChangeJournalInterceptor interceptor, Guid wallId, Barrier gate, double x)
    {
        using var scope = journal.BeginWallUpdateBatch(wallId);
        gate.SignalAndWait(TimeSpan.FromSeconds(30));
        await using var db = pg.CreateContext(interceptor);
        db.Holds.Add(new Hold { WallId = wallId, X = x, Y = x, Radius = 0.02, Generation = 1 });
        await db.SaveChangesAsync();
    }

    private static async Task AssertDistinctSeqAsync(PostgresTestDatabase pg, Guid wallId, int expected)
    {
        await using var db = pg.CreateContext();
        var batch = await db.ChangeJournalBatches.SingleAsync(b => b.ScopeId == wallId && b.Label == ChangeJournal.WallUpdateBatchLabel);
        var seqs = await db.ChangeJournalEntries.Where(e => e.BatchId == batch.Id).Select(e => e.Seq).ToListAsync();
        Assert.Equal(expected, seqs.Count);
        Assert.Equal(expected, seqs.Distinct().Count());
    }
}
