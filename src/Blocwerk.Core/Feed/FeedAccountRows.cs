// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Data;
using System.Linq.Expressions;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Helpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The feed side of an account erasure or merge (design §5.7), run inside the operation's transaction after its last
/// save. Both operations write in bulk across walls under a silent <see cref="FeedScope.BeginAccount"/>, so they write no
/// wall rows of their own: here every wall of the user's footprint (<see cref="FeedUserFootprint"/>, read before the
/// writes) gets one actorless <c>wall.resync</c>, and the stored rows that name the user are scrubbed (erasure) or deleted
/// (merge, whose target gets a <c>user.resync</c>).
/// <para>
/// Never fatal: each step runs under a savepoint and a failure is logged. The rows carry ids only, and an erased user's
/// row stays as an anonymous tombstone under the same id, so a missed scrub leaves nothing personal behind; retention
/// removes it within 30 days.
/// </para>
/// </summary>
internal static class FeedAccountRows
{
    public const string EraseReason = "account-erased";
    public const string MergeReason = "account-merged";

    /// <summary>The walls the operation on <paramref name="userId"/> will change, or none when the lookup failed.</summary>
    public static async Task<IReadOnlyList<Guid>> WallsAsync(BlocwerkDbContext db, Guid userId, ILogger logger)
    {
        try
        {
            return await FeedLookupSavepoint.RunAsync(db, () => FeedUserFootprint.WallsAsync(db, userId), CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Feed lookup of user {UserId}'s walls failed; their walls get no resync from this account change", userId);
            return [];
        }
    }

    /// <summary>
    /// Erasure: deletes the rows addressed to the user and the rows about their membership or profile, re-attributes the
    /// rest of their rows to the Ghost user, and resyncs <paramref name="walls"/>. The erased user's other devices reload on
    /// that resync and find their membership gone, so no <c>access.revoked</c> is written: it would keep their id in the feed.
    /// </summary>
    public static Task EraseAsync(BlocwerkDbContext db, Guid userId, IReadOnlyList<Guid> walls, ILogger logger) =>
        ApplyAsync(db, "account-erase", () => ScrubAsync(db, userId), [.. FeedBulkRows.Resyncs(walls, EraseReason)], logger);

    /// <summary>
    /// Merge: deletes every row by, to or about the source (moving them would hand the target the source's revocations,
    /// and its history is covered by the resyncs), resyncs <paramref name="walls"/>, and tells the target's own devices with
    /// one <c>user.resync</c>: their memberships, ratings and favourites changed.
    /// </summary>
    public static Task MergeAsync(BlocwerkDbContext db, Guid sourceUserId, Guid targetUserId, IReadOnlyList<Guid> walls, ILogger logger) =>
        ApplyAsync(
            db,
            "account-merge",
            () => ForgetAsync(db, sourceUserId),
            [.. FeedBulkRows.Resyncs(walls, MergeReason), FeedBulkRows.UserResync(targetUserId)],
            logger);

    /// <summary>
    /// After the erasure has committed: rows naming the user that their other devices committed while the erasure's
    /// serializable snapshot could not see them. Re-runs the scrub in a short read-committed transaction. Never throws.
    /// </summary>
    public static async Task ScrubCommittedAsync(BlocwerkDbContext db, Guid userId, ILogger logger)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, CancellationToken.None);
            await ScrubAsync(db, userId);
            await transaction.CommitAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Feed scrub after the erasure of user {UserId} failed; rows written meanwhile stay until retention", userId);
        }
    }

    private static async Task ApplyAsync(BlocwerkDbContext db, string site, Func<Task> rewrite, IReadOnlyList<WallChange> rows, ILogger logger)
    {
        try
        {
            await FeedLookupSavepoint.RunAsync(
                db,
                async () =>
                {
                    await rewrite();
                    return true;
                },
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Feed rewrite of {Site} failed; the user's feed rows stay until retention removes them", site);
        }

        await FeedBulkWrite.AppendAsync(db, site, rows, logger);
    }

    private static async Task ScrubAsync(BlocwerkDbContext db, Guid userId)
    {
        var none = CancellationToken.None;
        await db.WallChanges.Where(c => c.AudienceUserId == userId).ExecuteDeleteAsync(none);
        await db.WallChanges.Where(AboutUser(userId)).ExecuteDeleteAsync(none);
        await db.WallChanges.Where(c => c.ActorUserId == userId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ActorUserId, (Guid?)GhostUser.Id), none);
    }

    private static async Task ForgetAsync(BlocwerkDbContext db, Guid sourceUserId)
    {
        var none = CancellationToken.None;
        await db.WallChanges.Where(c => c.ActorUserId == sourceUserId || c.AudienceUserId == sourceUserId).ExecuteDeleteAsync(none);
        await db.WallChanges.Where(AboutUser(sourceUserId)).ExecuteDeleteAsync(none);
    }

    // The kinds whose entity is the user themselves.
    private static Expression<Func<WallChange, bool>> AboutUser(Guid userId) =>
        c => c.EntityId == userId && (c.Kind == FeedKinds.MemberChanged || c.Kind == FeedKinds.UserProfile);
}
