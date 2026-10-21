// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Comment compaction (design §5.4, B3): deleting a comment deletes its <c>comment.added</c> rows once the deletion is
/// written, in its transaction when there is one; a rollback keeps them, a failed save keeps them and its retry compacts,
/// a row already gone (retention, erasure) does not fail the deletion, and a collapsing scope still compacts.
/// </summary>
public sealed class SocialFeedCompactionTests
{
    [Fact]
    public Task ADeletion_RemovesTheAddedRow_InTheSameSave_Sqlite() => DeletionAsync(new SocialFeedFixture());

    [PostgresFact]
    public Task ADeletion_RemovesTheAddedRow_InTheSameSave_Postgres() => DeletionAsync(new SocialFeedFixture(PostgresTestDatabase.Create()));

    [Fact]
    public Task ARolledBackDeletion_KeepsTheAddedRow_Sqlite() => RollbackAsync(new SocialFeedFixture());

    [PostgresFact]
    public Task ARolledBackDeletion_KeepsTheAddedRow_Postgres() => RollbackAsync(new SocialFeedFixture(PostgresTestDatabase.Create()));

    [Fact]
    public Task AFailedDeletion_KeepsTheAddedRow_AndARetryCompactsOnce_Sqlite() => RetryAsync(new SocialFeedFixture());

    [PostgresFact]
    public Task AFailedDeletion_KeepsTheAddedRow_AndARetryCompactsOnce_Postgres() => RetryAsync(new SocialFeedFixture(PostgresTestDatabase.Create()));

    [Fact]
    public Task ACommittedTransaction_Compacts_Sqlite() => CommitAsync(new SocialFeedFixture());

    [PostgresFact]
    public Task ACommittedTransaction_Compacts_Postgres() => CommitAsync(new SocialFeedFixture(PostgresTestDatabase.Create()));

    [Fact]
    public Task AnAddedRowAlreadyGone_DoesNotFailTheDeletion_Sqlite() => AlreadyGoneAsync(new SocialFeedFixture());

    [PostgresFact]
    public Task AnAddedRowAlreadyGone_DoesNotFailTheDeletion_Postgres() => AlreadyGoneAsync(new SocialFeedFixture(PostgresTestDatabase.Create()));

    [Fact]
    public void TheCommentCompaction_FiltersByWall_SoItCanRunOnTheWallIndex()
    {
        using var db = TestDb.Create("Host=unused;Database=unused");

        var sql = FeedCommentCompaction.OfComments(db, [Guid.NewGuid()], [Guid.NewGuid()]).ToQueryString();

        var where = sql[sql.IndexOf("WHERE", StringComparison.Ordinal)..];
        Assert.Contains("\"WallId\"", where);
        Assert.Contains("\"EntityId\"", where);
    }

    [Fact]
    public async Task InsideAScope_TheDeletionCollapsesToAResync_AndStillCompacts()
    {
        using var fixture = new SocialFeedFixture();
        var comment = await AddCommentAsync(fixture);

        using (FeedScope.Begin(fixture.WallId, "merge"))
        {
            await DeleteAsync(fixture, comment);
        }

        var row = Assert.Single(await fixture.RowsAsync());
        Assert.Equal((FeedKinds.WallResync, (Guid?)fixture.WallId), (row.Kind, row.WallId));
    }

    private static async Task DeletionAsync(SocialFeedFixture fixture)
    {
        using var owned = fixture;
        var comment = await AddCommentAsync(fixture);
        var other = await AddCommentAsync(fixture);
        Assert.Equal(2, (await fixture.RowsAsync()).Count);

        await DeleteAsync(fixture, comment);

        var rows = await fixture.RowsAsync();
        Assert.Equal(
            [(FeedKinds.CommentAdded, other), (FeedKinds.CommentDeleted, comment)],
            rows.Select(r => (r.Kind, r.EntityId!.Value)));
    }

    private static async Task RollbackAsync(SocialFeedFixture fixture)
    {
        using var owned = fixture;
        var comment = await AddCommentAsync(fixture);

        await using (var db = fixture.Context())
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.BoulderComments.Remove(await db.BoulderComments.SingleAsync(c => c.Id == comment));
            await db.SaveChangesAsync();
            await tx.RollbackAsync();
        }

        var row = Assert.Single(await fixture.RowsAsync());
        Assert.Equal((FeedKinds.CommentAdded, (Guid?)comment), (row.Kind, row.EntityId));
    }

    private static async Task CommitAsync(SocialFeedFixture fixture)
    {
        using var owned = fixture;
        var comment = await AddCommentAsync(fixture);

        await using (var db = fixture.Context())
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            db.BoulderComments.Remove(await db.BoulderComments.SingleAsync(c => c.Id == comment));
            await db.SaveChangesAsync();
            await tx.CommitAsync();
        }

        var row = Assert.Single(await fixture.RowsAsync());
        Assert.Equal((FeedKinds.CommentDeleted, (Guid?)comment), (row.Kind, row.EntityId));
    }

    private static async Task AlreadyGoneAsync(SocialFeedFixture fixture)
    {
        using var owned = fixture;
        var comment = await AddCommentAsync(fixture);

        // Retention or an erasure deleted the comment.added row while the user was deleting the comment.
        await using var db = fixture.Context();
        var tracked = await db.BoulderComments.SingleAsync(c => c.Id == comment);
        await fixture.ClearRowsAsync();
        db.BoulderComments.Remove(tracked);
        await db.SaveChangesAsync();

        var row = Assert.Single(await fixture.RowsAsync());
        Assert.Equal((FeedKinds.CommentDeleted, (Guid?)comment), (row.Kind, row.EntityId));
    }

    private static async Task RetryAsync(SocialFeedFixture fixture)
    {
        using var owned = fixture;
        var comment = await AddCommentAsync(fixture);

        await using var db = fixture.Context();
        db.BoulderComments.Remove(await db.BoulderComments.SingleAsync(c => c.Id == comment));
        var duplicate = new User { Identifier = fixture.Harness.Owner.Identifier, DisplayName = "Duplicate" };
        db.Users.Add(duplicate);

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(FeedKinds.CommentAdded, Assert.Single(await fixture.RowsAsync()).Kind);

        db.Entry(duplicate).State = EntityState.Detached;
        await db.SaveChangesAsync();
        var row = Assert.Single(await fixture.RowsAsync());
        Assert.Equal((FeedKinds.CommentDeleted, (Guid?)comment), (row.Kind, row.EntityId));
    }

    private static async Task<Guid> AddCommentAsync(SocialFeedFixture fixture)
    {
        if (fixture.WallId == Guid.Empty)
        {
            await fixture.SeedAsync();
        }

        await using var db = fixture.Harness.CreateContext();
        var boulder = await db.Boulders.Select(b => b.Id).FirstAsync();
        var comment = new BoulderComment { BoulderId = boulder, UserId = fixture.OwnerId, Text = "beta" };
        await fixture.AddAsync(comment);
        return comment.Id;
    }

    private static async Task DeleteAsync(SocialFeedFixture fixture, Guid commentId)
    {
        await using var db = fixture.Context();
        db.BoulderComments.Remove(await db.BoulderComments.SingleAsync(c => c.Id == commentId));
        await db.SaveChangesAsync();
    }
}
