// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

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

    private static async Task<List<Core.Entities.WallChange>> RowsAsync(ContentHashBackfillKit kit)
    {
        await using var db = kit.H.CreateContext();
        return await db.WallChanges.AsNoTracking().ToListAsync();
    }
}
