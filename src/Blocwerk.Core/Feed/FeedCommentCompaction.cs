// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Runtime.CompilerServices;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// Comment compaction (design §5.4, B3): a save that writes <c>comment.deleted</c> deletes the comment's stored
/// <c>comment.added</c> rows once its own writes are done (<c>SavedChanges</c>), with one filtered delete that tolerates rows
/// already gone (retention, an erasure). A <c>boulder.deleted</c> compacts every <c>comment.added</c> of that boulder the same
/// way: the database deletes its comments by cascade, out of the tracker's sight, so they write no <c>comment.deleted</c>
/// of their own, and the boulder's deletion is what tells viewers they are gone. Inside an explicit transaction it runs before the commit, under a savepoint, and
/// rolls back with it; a failure only rolls back to the savepoint. Without one it runs right after EF's own transaction
/// committed, so a compaction can never outlive a deletion that rolled back; a crash in between leaves a
/// <c>comment.added</c> row that hydrates as a tombstone until retention removes it. It never fails the user's save.
/// </summary>
internal static class FeedCommentCompaction
{
    private const string Savepoint = "feed_comment_compaction";
    private static readonly ConditionalWeakTable<DbContext, FeedCompactionQueue> Queued = new();

    /// <summary>
    /// Queues the comments of the save's <c>comment.deleted</c> rows and the boulders of its <c>boulder.deleted</c> rows
    /// (before collapse, so a scope still compacts).
    /// </summary>
    public static void Collect(DbContext context, IReadOnlyList<WallChange> rows)
    {
        var comments = rows.Where(r => r.Kind == FeedKinds.CommentDeleted && r.EntityId is not null).Select(r => r.EntityId!.Value).ToList();
        var boulders = rows.Where(r => r.Kind == FeedKinds.BoulderDeleted && r.BoulderId is not null && r.WallId is not null)
            .Select(r => (r.WallId!.Value, r.BoulderId!.Value))
            .ToList();
        if (comments.Count > 0 || boulders.Count > 0)
        {
            var queue = Queued.GetValue(context, _ => new FeedCompactionQueue());
            queue.Comments.AddRange(comments);
            queue.Boulders.AddRange(boulders);
        }
    }

    /// <summary>Forgets the queue of a save that failed or was cancelled; a retry queues again.</summary>
    public static void Drop(DbContext context) => Queued.Remove(context);

    /// <summary>
    /// Runs the queued compaction. It never takes the request's token: the deletion it follows has already committed (or
    /// will with the caller's transaction), so a cancelled request must neither turn that success into an exception nor
    /// leave a savepoint open. Every failure, cancellation included, is logged and rolled back to the savepoint.
    /// </summary>
    public static async Task ApplyAsync(DbContext context, ILogger logger, bool async)
    {
        var none = CancellationToken.None;
        if (!Queued.TryGetValue(context, out var queued))
        {
            return;
        }

        Queued.Remove(context);
        var ids = queued.Comments.Distinct().ToList();
        var boulders = queued.Boulders.Distinct().ToList();
        var walls = boulders.Select(b => b.WallId).Distinct().ToList();
        var boulderIds = boulders.Select(b => b.BoulderId).ToList();
        var transaction = context.Database.CurrentTransaction;
        var savepoint = transaction is { SupportsSavepoints: true };
        try
        {
            if (savepoint)
            {
                await Run(async, () => transaction!.CreateSavepointAsync(Savepoint, none), () => transaction!.CreateSavepoint(Savepoint));
            }

            if (ids.Count > 0)
            {
                var rows = context.Set<WallChange>()
                    .Where(c => c.Kind == FeedKinds.CommentAdded && c.EntityId != null && ids.Contains(c.EntityId.Value));
                _ = async ? await rows.ExecuteDeleteAsync(none) : rows.ExecuteDelete();
            }

            if (boulderIds.Count > 0)
            {
                IQueryable<WallChange> rows = OfBoulders(context, walls, boulderIds);
                _ = async ? await rows.ExecuteDeleteAsync(none) : rows.ExecuteDelete();
            }

            if (savepoint)
            {
                await Run(async, () => transaction!.ReleaseSavepointAsync(Savepoint, none), () => transaction!.ReleaseSavepoint(Savepoint));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(
                ex, "Feed comment compaction failed for {Count} comment(s) and {Boulders} boulder(s); their comment.added rows stay as tombstones", ids.Count, boulders.Count);
            if (savepoint)
            {
                await RollBackAsync(transaction!, async, logger);
            }
        }
    }

    /// <summary>
    /// The <c>comment.added</c> rows of deleted boulders. Filtered by wall first, so it runs on the <c>(WallId, TxId, Seq)</c>
    /// index instead of scanning the table inside the user's transaction on every boulder delete.
    /// </summary>
    internal static IQueryable<WallChange> OfBoulders(DbContext context, List<Guid> walls, List<Guid> boulders) =>
        context.Set<WallChange>().Where(c => c.WallId != null && walls.Contains(c.WallId.Value)
            && c.Kind == FeedKinds.CommentAdded && c.BoulderId != null && boulders.Contains(c.BoulderId.Value));

    private static async Task RollBackAsync(Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction, bool async, ILogger logger)
    {
        try
        {
            await Run(async, () => transaction.RollbackToSavepointAsync(Savepoint), () => transaction.RollbackToSavepoint(Savepoint));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Rolling back the feed compaction savepoint failed");
        }
    }

    private static Task Run(bool async, Func<Task> asyncCall, Action syncCall)
    {
        if (async)
        {
            return asyncCall();
        }

        syncCall();
        return Task.CompletedTask;
    }
}
