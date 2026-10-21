// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Data;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.Extensions.Logging;

namespace Blocwerk.Core.Assets.Manifest;

/// <summary>
/// The lazy-sha half of the backfill (design §6.2, "when a wall's backfill completes, emit one <c>wall.manifest</c>"). The
/// backfill writes its hashes with raw SQL, which no mapper sees, so the walls that listed unhashed rows before a pass
/// are told afterwards: one actorless <c>wall.manifest</c> per wall, which makes open clients refetch and the server's
/// cached manifest of the wall rebuild (<see cref="ManifestFreshness"/>).
/// </summary>
/// <remarks>
/// Never fatal, like every feed side step: a failed announcement is logged, and the clients pick the hashes up on their
/// next manifest fetch anyway (an entry listing unhashed rows lives only <see cref="WallManifestService.PendingMaxAge"/>).
/// </remarks>
public sealed class ManifestBackfillAnnouncer(Func<BlocwerkDbContext> createContext, IWallFeed feed, ILogger logger)
{
    /// <summary>Walls announced per transaction.</summary>
    public const int Batch = 200;

    /// <summary>The walls that list unhashed rows now; empty when they cannot be read.</summary>
    public async Task<IReadOnlyCollection<Guid>> PendingAsync(CancellationToken ct)
    {
        try
        {
            await using var db = createContext();
            return await ManifestPendingWalls.QueryAsync(db, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not list the walls with unhashed assets; their manifests refresh on their own");
            return [];
        }
    }

    /// <summary>Appends one <c>wall.manifest</c> per wall, in transactions of <see cref="Batch"/> walls.</summary>
    public async Task AnnounceAsync(IReadOnlyCollection<Guid> wallIds, CancellationToken ct)
    {
        foreach (var chunk in wallIds.Order().Chunk(Batch))
        {
            try
            {
                await using var db = createContext();
                await using var tx = await db.Database.BeginTransactionAsync(ct);
                var now = DateTimeOffset.UtcNow;
                foreach (var wallId in chunk)
                {
                    await feed.AppendAsync(db, Row(wallId, now), ct);
                }

                await tx.CommitAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Could not announce the new hashes of {Count} walls; their manifests refresh on their own", chunk.Length);
            }
        }
    }

    private static WallChange Row(Guid wallId, DateTimeOffset now) => new()
    {
        WallId = wallId,
        Kind = FeedKinds.WallManifest,
        EntityId = wallId,
        Audience = FeedAudience.Wall,
        SchemaVersion = FeedSchema.PayloadVersion,
        CreatedAt = now,
    };
}
