// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Social;

/// <summary>
/// <c>gallery.changed</c> (an image added, or what the gallery shows of it changed: caption, file, type, size, capture
/// time) and <c>gallery.deleted</c>, by image id. A write of the content hash alone (the PR 2.2 backfill) changes
/// nothing a viewer sees and maps to nothing; a rewrite of the file itself also changes its size or path.
/// </summary>
internal sealed class GalleryFeedMapper : IFeedMapper
{
    private static readonly IReadOnlySet<string> Visible = new HashSet<string>
    {
        nameof(WallImage.Caption), nameof(WallImage.StoragePath), nameof(WallImage.ContentType), nameof(WallImage.SizeBytes),
        nameof(WallImage.CapturedAt), nameof(WallImage.WallId),
    };

    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(WallImage)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        if (!SocialRows.Changed(entry, Visible))
        {
            return;
        }

        var image = (WallImage)entry.Entity;
        context.Add(new WallChange
        {
            WallId = image.WallId,
            Kind = entry.State == EntityState.Deleted ? FeedKinds.GalleryDeleted : FeedKinds.GalleryChanged,
            EntityId = image.Id,
            Audience = FeedAudience.Wall,
        });
    }
}
