// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The social family's step after <see cref="FeedBoulderResolver"/> and before collapse: a wall-less <c>user.profile</c>
/// row for a shared audience becomes one row per wall of the user (<see cref="FeedUserWalls"/>), and a
/// <c>comment.deleted</c> row queues the compaction of its <c>comment.added</c> rows (<see cref="FeedCommentCompaction"/>).
/// It queries only when a row asks for it, and a failed lookup never fails the save: the rows it could not expand are
/// dropped and the error is logged.
/// </summary>
internal static class FeedResolve
{
    public static async Task<IReadOnlyList<WallChange>> ApplyAsync(
        DbContext context, IReadOnlyList<WallChange> rows, ILogger logger, bool async, CancellationToken cancellationToken)
    {
        FeedCommentCompaction.Collect(context, rows);
        if (!rows.Any(FeedUserWalls.Needs))
        {
            return rows;
        }

        try
        {
            return await FeedUserWalls.ApplyAsync(context, rows, async, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Feed lookup of a user's walls failed; their profile change reaches only their own devices");
            return rows.Where(r => !FeedUserWalls.Needs(r)).ToList();
        }
    }
}
