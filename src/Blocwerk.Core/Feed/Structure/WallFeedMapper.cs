// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Contracts.Feed.Payloads;
using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace Blocwerk.Core.Feed.Structure;

/// <summary>
/// <see cref="Entities.Wall"/>: <c>wall.updated</c> with the live generation (settings, photo, shape, maintenance, a reset).
/// A save that only touches the staged photo or the linked-hold sync marker writes nothing: no viewer sees those columns.
/// A deleted wall writes a <c>wall.resync</c>, so its open views reload and find it gone.
/// </summary>
internal sealed class WallFeedMapper : IFeedMapper
{
    /// <summary>The resync reason of a deleted wall.</summary>
    public const string DeletedReason = "wall-deleted";

    private static readonly IReadOnlySet<string> Unseen = new HashSet<string>(StringComparer.Ordinal)
    {
        nameof(Wall.StagedPhoto), nameof(Wall.StagedPhotoContentType), nameof(Wall.StagedAt), nameof(Wall.StagedByUserId),
        nameof(Wall.LinkedHoldSyncVersion),
    };

    public IReadOnlyCollection<Type> EntityTypes { get; } = [typeof(Wall)];

    public void Map(EntityEntry entry, FeedMapContext context)
    {
        var wall = (Wall)entry.Entity;
        if (entry.State == EntityState.Deleted)
        {
            context.Add(FeedRows.Resync(wall.Id, DeletedReason, context.ActorUserId, context.Now));
            return;
        }

        if (FeedEntryValues.OnlyModified(entry, Unseen))
        {
            return;
        }

        context.Add(StructureRows.Wall(FeedKinds.WallUpdated, wall.Id, wall.Id, new GenerationPayload(wall.CurrentGeneration)));
    }
}
