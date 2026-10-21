// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Social;

/// <summary>
/// <c>video.added</c> (credited to the uploader), <c>video.status</c> when what a player shows changed (encoding state,
/// HLS, thumbnail, the stored file), and <c>video.deleted</c>. Bookkeeping-only updates (an error text, a timestamp)
/// map to nothing. The bulk status writes of the normalizer are PR 1.3's bulk sites.
/// </summary>
internal sealed class BetaVideoFeedMapper : IFeedMapper
{
    private static readonly IReadOnlySet<string> Visible = new HashSet<string>
    {
        nameof(BetaVideo.EncodingStatus), nameof(BetaVideo.HasHls), nameof(BetaVideo.Thumbnail),
        nameof(BetaVideo.StoragePath), nameof(BetaVideo.Data), nameof(BetaVideo.BoulderId),
    };

    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(BetaVideo)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        if (!SocialRows.Changed(entry, Visible))
        {
            return;
        }

        var video = (BetaVideo)entry.Entity;
        var kind = entry.State switch
        {
            EntityState.Added => FeedKinds.VideoAdded,
            EntityState.Deleted => FeedKinds.VideoDeleted,
            _ => FeedKinds.VideoStatus,
        };
        var payload = new VideoPayload(video.BoulderId, video.EncodingStatus.ToString(), video.HasHls);
        var row = SocialRows.OnBoulder(video.BoulderId, kind, video.Id, payload);
        if (entry.State == EntityState.Added)
        {
            row.ActorUserId = video.UploadedByUserId;
        }

        context.Add(row);
    }
}
