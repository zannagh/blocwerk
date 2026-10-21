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
/// thumbnail writes) with their <c>video.deleted</c> / <c>video.status</c> rows, through <see cref="FeedBulkWrite"/>. The
/// clips are read with the write's own predicate, inside its transaction, just before it runs.
/// </summary>
internal static class FeedVideoBulk
{
    /// <summary>Deletes <paramref name="target"/> and writes a <c>video.deleted</c> per deleted clip. Returns the count.</summary>
    public static Task<int> DeleteAsync(BlocwerkDbContext db, IQueryable<BetaVideo> target, ILogger logger, CancellationToken ct) =>
        FeedBulkWrite.RunAsync(
            db,
            "video-delete",
            () => FactsAsync(target),
            () => target.ExecuteDeleteAsync(ct),
            (videos, deleted) => deleted == 0 ? [] : videos.Select(v => FeedBulkRows.Video(v, FeedKinds.VideoDeleted, v.EncodingStatus, v.HasHls)),
            logger,
            ct);

    /// <summary>
    /// Runs <paramref name="write"/>, which moves the clips of <paramref name="target"/> to <paramref name="status"/> (and
    /// <paramref name="hasHls"/>, when it sets that), and writes a <c>video.status</c> per clip it changed.
    /// </summary>
    public static Task<int> StatusAsync(
        BlocwerkDbContext db,
        string site,
        IQueryable<BetaVideo> target,
        Func<Task<int>> write,
        BetaVideoEncodingStatus status,
        bool? hasHls,
        ILogger logger,
        CancellationToken ct) =>
        FeedBulkWrite.RunAsync(
            db,
            site,
            () => FactsAsync(target),
            write,
            (videos, changed) => changed == 0 ? [] : videos.Select(v => FeedBulkRows.Video(v, FeedKinds.VideoStatus, status, hasHls ?? v.HasHls)),
            logger,
            ct);

    private static Task<List<FeedVideoFacts>> FactsAsync(IQueryable<BetaVideo> target) =>
        target.AsNoTracking()
            .Select(v => new FeedVideoFacts(v.Id, v.BoulderId, v.Boulder.WallId, v.Boulder.IsDraft, v.EncodingStatus, v.HasHls))
            .ToListAsync(CancellationToken.None);
}
