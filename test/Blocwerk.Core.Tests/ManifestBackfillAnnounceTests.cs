// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Assets.Manifest;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The lazy-sha half of the manifest: the backfill hashes in raw SQL, which no mapper sees, so a pass that hashed
/// anything announces each wall that listed unhashed rows with one actorless <c>wall.manifest</c>.
/// </summary>
public class ManifestBackfillAnnounceTests
{
    [Fact]
    public async Task APassThatHashedRows_AnnouncesTheirWall_Once()
    {
        using var kit = new ContentHashBackfillKit(null);
        await kit.H.SeedWallAsync(holdCount: 0);
        await kit.NullEveryHashAsync();
        var feed = new WallFeed(() => kit.H.RootContextFactory.CreateDbContext(), NullLogger<WallFeed>.Instance);

        var count = await kit.Backfill(feed).RunPassAsync(default);

        Assert.True(count.Stamped > 0);
        var row = Assert.Single(await RowsAsync(kit));
        Assert.Equal((kit.H.WallId, FeedKinds.WallManifest, FeedAudience.Wall, (Guid?)null), (row.WallId, row.Kind, row.Audience, row.ActorUserId));

        await kit.Backfill(feed).RunPassAsync(default);
        Assert.Single(await RowsAsync(kit));
    }

    [Fact]
    public async Task WithoutAFeed_TheBackfillWritesNoRows()
    {
        using var kit = new ContentHashBackfillKit(null);
        await kit.H.SeedWallAsync(holdCount: 0);
        await kit.NullEveryHashAsync();

        Assert.True((await kit.Backfill().RunPassAsync(default)).Stamped > 0);
        Assert.Empty(await RowsAsync(kit));
    }

    /// <summary>A ladder counts as pending only while one of its levels has no sha, not merely for existing.</summary>
    [Fact]
    public async Task AWallIsPending_OnlyWhileALadderLevelLacksItsSha()
    {
        using var kit = new ContentHashBackfillKit(null);
        await kit.H.SeedWallAsync(holdCount: 0);
        var hashed = SplatLodLadder.Serialize([new SplatLodLevel(40_000, "a.spz", 2, new string('a', 64)), new SplatLodLevel(120_000, "b.spz", 3, new string('b', 64))]);
        var (splat, _) = await kit.AddGeometryAsync(hashed);
        await using (var db = kit.H.CreateContext())
        {
            await db.WallGeometrySplats.ExecuteUpdateAsync(u => u.SetProperty(x => x.Sha256, new byte[32]).SetProperty(x => x.MobileSha256, new byte[32]));
            await db.WallGeometryTextures.ExecuteUpdateAsync(u => u.SetProperty(x => x.Sha256, new byte[32]).SetProperty(x => x.MaskSha256, new byte[32]));
        }

        var announcer = new ManifestBackfillAnnouncer(() => kit.H.RootContextFactory.CreateDbContext(), Substitute.For<IWallFeed>(), NullLogger.Instance);
        Assert.Empty(await announcer.PendingAsync(default));

        var missing = SplatLodLadder.Serialize([new SplatLodLevel(40_000, "a.spz", 2, new string('a', 64)), new SplatLodLevel(120_000, "b.spz", 3)]);
        await using (var db = kit.H.CreateContext())
        {
            await db.WallGeometrySplats.Where(x => x.Id == splat).ExecuteUpdateAsync(u => u.SetProperty(x => x.LodLevelsJson, missing));
        }

        Assert.Equal([kit.H.WallId], await announcer.PendingAsync(default));
    }

    private static async Task<List<Core.Entities.WallChange>> RowsAsync(ContentHashBackfillKit kit)
    {
        await using var db = kit.H.CreateContext();
        return await db.WallChanges.AsNoTracking().ToListAsync();
    }
}
