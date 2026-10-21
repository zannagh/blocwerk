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
/// already gone (retention, an erasure). Inside an explicit transaction it runs before the commit, under a savepoint, and
/// rolls back with it; a failure only rolls back to the savepoint. Without one it runs right after EF's own transaction
/// committed, so a compaction can never outlive a deletion that rolled back; a crash in between leaves a
/// <c>comment.added</c> row that hydrates as a tombstone until retention removes it. It never fails the user's save.
/// </summary>
internal static class FeedCommentCompaction
{
    private const string Savepoint = "feed_comment_compaction";
    private static readonly ConditionalWeakTable<DbContext, List<Guid>> Queued = new();

    /// <summary>Queues the comments of the save's <c>comment.deleted</c> rows (before collapse, so a scope still compacts).</summary>
    public static void Collect(DbContext context, IReadOnlyList<WallChange> rows)
    {
        var ids = rows.Where(r => r.Kind == FeedKinds.CommentDeleted && r.EntityId is not null).Select(r => r.EntityId!.Value).ToList();
        if (ids.Count > 0)
        {
            Queued.GetValue(context, _ => []).AddRange(ids);
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
        var ids = queued.Distinct().ToList();
        var transaction = context.Database.CurrentTransaction;
        var savepoint = transaction is { SupportsSavepoints: true };
        try
        {
            if (savepoint)
            {
                await Run(async, () => transaction!.CreateSavepointAsync(Savepoint, none), () => transaction!.CreateSavepoint(Savepoint));
            }

            var rows = context.Set<WallChange>()
                .Where(c => c.Kind == FeedKinds.CommentAdded && c.EntityId != null && ids.Contains(c.EntityId.Value));
            _ = async ? await rows.ExecuteDeleteAsync(none) : rows.ExecuteDelete();
            if (savepoint)
            {
                await Run(async, () => transaction!.ReleaseSavepointAsync(Savepoint, none), () => transaction!.ReleaseSavepoint(Savepoint));
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Feed comment compaction failed for {Count} comment(s); their comment.added rows stay as tombstones", ids.Count);
            if (savepoint)
            {
                await RollBackAsync(transaction!, async, logger);
            }
        }
    }

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
