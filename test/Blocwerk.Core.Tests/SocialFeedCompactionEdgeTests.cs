// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Compaction is never fatal: a request cancelled once the deletion committed still succeeds and still compacts, and a
/// synchronous save compacts as an asynchronous one does, with and without a transaction.
/// </summary>
public sealed class SocialFeedCompactionEdgeTests
{
    [Fact]
    public Task ACancelAfterTheCommit_NeitherThrowsNorSkipsTheCompaction_Sqlite() => CancelAsync(new SocialFeedFixture());

    [PostgresFact]
    public Task ACancelAfterTheCommit_NeitherThrowsNorSkipsTheCompaction_Postgres() => CancelAsync(new SocialFeedFixture(PostgresTestDatabase.Create()));

    [Fact]
    public Task ASynchronousSave_Compacts_Sqlite() => SyncAsync(new SocialFeedFixture(), transaction: false);

    [PostgresFact]
    public Task ASynchronousSave_Compacts_Postgres() => SyncAsync(new SocialFeedFixture(PostgresTestDatabase.Create()), transaction: false);

    [PostgresFact]
    public Task ASynchronousSaveInATransaction_Compacts_Postgres() => SyncAsync(new SocialFeedFixture(PostgresTestDatabase.Create()), transaction: true);

    private static async Task CancelAsync(SocialFeedFixture fixture)
    {
        using var owned = fixture;
        var comment = await AddCommentAsync(fixture);
        using var cts = new CancellationTokenSource();

        await using (var db = fixture.Context(fixture.OwnerId, NullLogger<Blocwerk.Core.Feed.FeedInterceptor>.Instance, new CancelOnCommit(cts)))
        {
            db.BoulderComments.Remove(await db.BoulderComments.SingleAsync(c => c.Id == comment));
            await db.SaveChangesAsync(cts.Token);
        }

        Assert.True(cts.IsCancellationRequested);
        await using var check = fixture.Harness.CreateContext();
        Assert.False(await check.BoulderComments.AnyAsync(c => c.Id == comment));
        Assert.Equal([FeedKinds.CommentDeleted], (await fixture.RowsAsync()).Select(r => r.Kind));
    }

    private static async Task SyncAsync(SocialFeedFixture fixture, bool transaction)
    {
        using var owned = fixture;
        var comment = await AddCommentAsync(fixture);

        var save = Task.Run(() =>
        {
            using var db = fixture.Context(fixture.OwnerId);
            using var tx = transaction ? db.Database.BeginTransaction() : null;
            db.BoulderComments.Remove(db.BoulderComments.Single(c => c.Id == comment));
            db.SaveChanges();
            tx?.Commit();
        });
        Assert.True(await Task.WhenAny(save, Task.Delay(20000)) == save, "the synchronous save hung");
        await save;

        Assert.Equal([FeedKinds.CommentDeleted], (await fixture.RowsAsync()).Select(r => r.Kind));
    }

    private static async Task<Guid> AddCommentAsync(SocialFeedFixture fixture)
    {
        var boulder = await fixture.SeedAsync();
        var comment = new BoulderComment { BoulderId = boulder, UserId = fixture.OwnerId, Text = "beta" };
        await fixture.AddAsync(comment);
        return comment.Id;
    }
}
