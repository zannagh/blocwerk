// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Feed;

/// <summary>
/// The bulk beta-video writes (the uploader's delete, the admin's re-encode queue, the normalizer's status, file and
/// thumbnail writes) with their <c>video.deleted</c> / <c>video.status</c> rows, through <see cref="FeedBulkWrite"/>.
/// <para>
/// The rows describe exactly the clips the write changed. Inside the write's transaction the clips matching its predicate
/// are read, locked (<c>FOR UPDATE</c> on Postgres, in id order), and read again with the predicate, so a clip another
/// transaction changed meanwhile is judged on its committed state. The write is then narrowed to exactly those ids: a clip
/// that stopped matching is neither written nor reported, and one that started matching is left for the next run. When the
/// lookup fails, the write runs over its whole predicate as before, without rows.
/// </para>
/// </summary>
internal static class FeedVideoBulk
{
    /// <summary>Deletes <paramref name="target"/> and writes a <c>video.deleted</c> per deleted clip. Returns the count.</summary>
    public static Task<int> DeleteAsync(BlocwerkDbContext db, IQueryable<BetaVideo> target, ILogger logger, CancellationToken ct) =>
        FeedBulkWrite.RunAsync(
            db,
            "video-delete",
            () => LockedFactsAsync(db, target),
            (List<FeedVideoFacts>? facts) => Narrow(target, facts).ExecuteDeleteAsync(ct),
            (videos, deleted) => deleted == 0 ? [] : videos.Select(v => FeedBulkRows.Video(v, FeedKinds.VideoDeleted, v.EncodingStatus, v.HasHls)),
            logger,
            ct);

    /// <summary>
    /// Runs <paramref name="write"/> over the clips of <paramref name="target"/> it is handed (narrowed to the locked ones),
    /// which moves them to <paramref name="status"/> (and <paramref name="hasHls"/>, when it sets that), and writes a
    /// <c>video.status</c> per clip.
    /// </summary>
    public static Task<int> StatusAsync(
        BlocwerkDbContext db,
        string site,
        IQueryable<BetaVideo> target,
        Func<IQueryable<BetaVideo>, Task<int>> write,
        BetaVideoEncodingStatus status,
        bool? hasHls,
        ILogger logger,
        CancellationToken ct) =>
        FeedBulkWrite.RunAsync(
            db,
            site,
            () => LockedFactsAsync(db, target),
            (List<FeedVideoFacts>? facts) => write(Narrow(target, facts)),
            (videos, changed) => changed == 0 ? [] : videos.Select(v => FeedBulkRows.Video(v, FeedKinds.VideoStatus, status, hasHls ?? v.HasHls)),
            logger,
            ct);

    private static IQueryable<BetaVideo> Narrow(IQueryable<BetaVideo> target, List<FeedVideoFacts>? facts)
    {
        if (facts is null)
        {
            return target;
        }

        var ids = facts.Select(f => f.Id).ToList();
        return target.Where(v => ids.Contains(v.Id));
    }

    private static async Task<List<FeedVideoFacts>> LockedFactsAsync(BlocwerkDbContext db, IQueryable<BetaVideo> target)
    {
        var none = CancellationToken.None;
        var ids = await target.Select(v => v.Id).ToListAsync(none);
        if (ids.Count == 0)
        {
            return [];
        }

        if (db.Database.IsNpgsql())
        {
            await db.Database.ExecuteSqlRawAsync(
                "SELECT 1 FROM \"BetaVideos\" WHERE \"Id\" = ANY({0}) ORDER BY \"Id\" FOR UPDATE", [ids.ToArray()], none);
        }

        return await target.AsNoTracking()
            .Where(v => ids.Contains(v.Id))
            .Select(v => new FeedVideoFacts(v.Id, v.BoulderId, v.Boulder.WallId, v.Boulder.IsDraft, v.EncodingStatus, v.HasHls))
            .ToListAsync(none);
    }
}
