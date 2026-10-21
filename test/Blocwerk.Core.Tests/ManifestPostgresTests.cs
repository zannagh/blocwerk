// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Assets;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>The manifest on Postgres: the per-audience lists, the cache's cursor check with real xids, and the backfill's announcement.</summary>
public class ManifestPostgresTests
{
    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task EveryAudience_ListsOnlyWhatItCanFetch_OnPostgres()
    {
        using var f = new ManifestFixture(PostgresTestDatabase.Create());
        var s = await f.SeedAsync();

        var member = await f.GetAsync();
        f.Assets.Caller = null;
        var share = await f.GetAsync(AssetRouteFixture.ShareToken);
        f.BindKiosk();
        var kiosk = await f.GetAsync();

        Assert.Equal(ManifestAudienceTests.Everything(s).Order(), ManifestAudienceTests.Shas(member).Order());
        Assert.DoesNotContain(s.DraftVideoThumbnail, ManifestAudienceTests.Shas(share));
        Assert.DoesNotContain(kiosk!.Assets, a => a.Kind == ManifestAssetKinds.VideoThumbnail);
        foreach (var sha in ManifestAudienceTests.Shas(kiosk))
        {
            Assert.Equal(StatusCodes.Status200OK, (await f.Assets.GetAsync(f.WallId, sha, method: HttpMethods.Head)).Response.StatusCode);
        }
    }

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task TheCacheCheck_ReadsTheWallsRowsAfterItsCursor_OnPostgres()
    {
        using var f = new ManifestFixture(PostgresTestDatabase.Create());
        await f.SeedAsync();
        var first = (await f.GetAsync())!;
        Assert.Matches(@"^[0-9a-f]{32}\.\d+\.\d+$", first.Cursor!);
        await using (var db = f.Assets.Harness.CreateContext())
        {
            await db.WallResets.ExecuteUpdateAsync(u => u.SetProperty(r => r.PreviousPhotoSha256, (byte[]?)null));
        }

        await f.AddFeedRowAsync(FeedKinds.CommentAdded);
        Assert.Equal(first.Sha, (await f.GetAsync())!.Sha);
        await f.AddFeedRowAsync(FeedKinds.WallManifest);
        Assert.Equal(1, (await f.GetAsync())!.Pending);
    }

    [PostgresFact]
    [Trait("Db", "Postgres")]
    public async Task ABackfillPass_AnnouncesTheWallsItHashed_OnPostgres()
    {
        using var kit = new ContentHashBackfillKit(PostgresTestDatabase.Create());
        await kit.H.SeedWallAsync(holdCount: 0);
        await kit.NullEveryHashAsync();
        var feed = new WallFeed(() => kit.H.RootContextFactory.CreateDbContext(), NullLogger<WallFeed>.Instance);

        Assert.True((await kit.Backfill(feed).RunPassAsync(default)).Stamped > 0);

        await using var db = kit.H.CreateContext();
        var row = await db.WallChanges.AsNoTracking().SingleAsync();
        Assert.Equal((kit.H.WallId, FeedKinds.WallManifest, FeedAudience.Wall), (row.WallId, row.Kind, row.Audience));
        Assert.NotNull(row.TxId);
    }
}
