// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Comments deleted by the database's cascade (a boulder deleted) write no <c>comment.deleted</c> of their own: the
/// boulder's <c>boulder.deleted</c> compacts their <c>comment.added</c> rows instead (PR 1.3), and leaves other boulders'
/// rows alone. The compaction filters by wall first, so it uses the wall index.
/// </summary>
public sealed class FeedBulkCascadeTests
{
    [Fact]
    public Task DeletingABoulder_CompactsItsCommentRows_Sqlite() => CascadeAsync(new SocialFeedFixture());

    [PostgresFact]
    public Task DeletingABoulder_CompactsItsCommentRows_Postgres() => CascadeAsync(new SocialFeedFixture(PostgresTestDatabase.Create()));

    [Fact]
    public void TheBoulderCompaction_FiltersByWall_SoItCanRunOnTheWallIndex()
    {
        using var db = TestDb.Create("Host=unused;Database=unused");

        var sql = FeedCommentCompaction.OfBoulders(db, [Guid.NewGuid()], [Guid.NewGuid()]).ToQueryString();

        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
        Assert.Contains("\"WallId\"", where);
        Assert.Contains("\"BoulderId\"", where);
    }

    private static async Task CascadeAsync(SocialFeedFixture fixture)
    {
        using var owned = fixture;
        var doomed = await fixture.SeedAsync();
        var kept = await fixture.AddBoulderAsync();
        await fixture.AddAsync(new BoulderComment { BoulderId = doomed, UserId = fixture.OwnerId, Text = "gone" }, fixture.OwnerId);
        await fixture.AddAsync(new BoulderComment { BoulderId = kept, UserId = fixture.OwnerId, Text = "stays" }, fixture.OwnerId);

        await using (var db = fixture.Context(fixture.OwnerId))
        {
            db.Boulders.Remove(await db.Boulders.SingleAsync(b => b.Id == doomed));
            await db.SaveChangesAsync();
        }

        var rows = await fixture.RowsAsync();
        Assert.Equal(kept, Assert.Single(rows, r => r.Kind == FeedKinds.CommentAdded).BoulderId);
        Assert.Contains(rows, r => r.Kind == FeedKinds.BoulderDeleted && r.BoulderId == doomed);
        await using var check = fixture.Harness.CreateContext();
        Assert.False(await check.BoulderComments.AnyAsync(c => c.BoulderId == doomed));
    }
}
