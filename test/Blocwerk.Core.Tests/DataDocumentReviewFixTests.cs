// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using static Blocwerk.Core.Tests.DataDocumentFreshnessTests;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The 3.1 review's fixes: historic and archived boulders as the wall page lists them, one build per document under
/// concurrency, kiosk no-store, renames of setters who left, a wall deleted mid-build, and the epoch order of builds.
/// </summary>
public sealed class DataDocumentReviewFixTests
{
    [Fact]
    public async Task HistoricBoulders_AreInEveryClass_ArchivedOnes_OnlyForTheWallsMembers()
    {
        using var f = await SeededAsync();
        f.Caller = f.Member;
        var members = await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders);
        f.Caller = null;
        var outsider = await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders, DataDocumentFixture.ShareToken);

        Assert.True(members.Boulders.Single(b => b.Id == f.HistoricId).IsHistoric);
        Assert.True(members.Boulders.Single(b => b.Id == f.ArchivedId).IsArchived);
        Assert.False(members.Boulders.Single(b => b.Id == f.PublishedId).IsHistoric);
        Assert.True(outsider.Boulders.Single(b => b.Id == f.HistoricId).IsHistoric);
        Assert.DoesNotContain(outsider.Boulders, b => b.Id == f.ArchivedId || b.IsArchived);
    }

    /// <summary>Eight callers that all find the wall stale share one build per document: four marks, not thirty-two.</summary>
    [Fact]
    public async Task ConcurrentCallers_ShareOneBuildPerDocument()
    {
        using var f = await SeededAsync();
        var feed = new WriteAfterMarkFeed(f.Feed);
        f.Feed = feed;
        var release = new TaskCompletionSource();
        feed.Arm(() => release.Task);

        var callers = Enumerable.Range(0, 8).Select(_ => Task.Run(() => f.IndexAsync())).ToList();
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (f.Flights.Count == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        await Task.Delay(300); // every caller is waiting on the first build now
        release.SetResult();
        var indexes = await Task.WhenAll(callers);

        Assert.Equal(DataDocumentKinds.All.Count, feed.Marks);
        Assert.Single(indexes.Select(i => i.Cursor).Distinct());
        Assert.Equal(0, f.Flights.Count);
    }

    [Fact]
    public void AKioskThatMayNotCacheItsWall_GetsNoStore_AnyoneElseDoesNot()
    {
        var wallId = Guid.NewGuid();
        var keyless = Substitute.For<IKioskContext>();
        keyless.IsKiosk.Returns(true);
        keyless.KioskWallId.Returns(wallId);
        var bound = Substitute.For<IKioskContext>();
        bound.IsKiosk.Returns(true);
        bound.KioskWallId.Returns(wallId);
        bound.KioskApiKeyId.Returns(Guid.NewGuid());

        Assert.True(DocumentCaching.NoStore(keyless, wallId));
        Assert.True(DocumentCaching.NoStore(bound, Guid.NewGuid()));
        Assert.False(DocumentCaching.NoStore(bound, wallId));
        Assert.False(DocumentCaching.NoStore(Substitute.For<IKioskContext>(), wallId));
        Assert.False(DocumentCaching.NoStore(null, wallId));
    }

    [Fact]
    public async Task ABoundKiosk_GetsItsDocumentsCacheable()
    {
        using var f = await SeededAsync();
        f.Caller = null;
        f.BindKiosk(f.WallId);
        var sha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Holds);

        var http = await f.DocumentHttpAsync(DataDocumentKinds.Holds, sha);

        Assert.Equal("private, no-cache", http.Response.Headers.CacheControl.ToString());
        Assert.Equal($"\"{sha}\"", http.Response.Headers.ETag.ToString());
    }

    /// <summary>
    /// The gate and the cache rules are asked separately: a tablet the gate admits but <c>KioskCacheRules</c> refuses (here
    /// its key id reads null on the second look, as a future per-wall switch would answer) gets the bytes <c>no-store</c>.
    /// </summary>
    [Fact]
    public async Task AKioskTheCacheRulesRefuse_GetsTheDocumentNoStore()
    {
        using var f = await SeededAsync();
        f.Caller = null;
        f.BindKiosk(f.WallId);
        var sha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Holds);
        f.Kiosk.KioskApiKeyId.Returns(f.KioskKeyId, (Guid?)null);

        var http = await f.DocumentHttpAsync(DataDocumentKinds.Holds, sha);

        Assert.Equal(200, http.Response.StatusCode);
        Assert.Equal("no-store", http.Response.Headers.CacheControl.ToString());
        Assert.Equal("Cookie, Accept-Encoding", http.Response.Headers.Vary.ToString());
        Assert.True(string.IsNullOrEmpty(http.Response.Headers.ETag.ToString()));
    }

    /// <summary>A setter who left the wall is still its byline; their rename reaches no wall row, only their own.</summary>
    [Fact]
    public async Task ARenameOfASetterWhoLeftTheWall_RebuildsTheBoulders()
    {
        using var f = await SeededAsync();
        f.Caller = f.Member;
        await f.IndexAsync();
        await using (var db = f.Harness.CreateContext())
        {
            await db.WallMembers.Where(m => m.UserId == f.Setter.Id && m.WallId == f.WallId).ExecuteDeleteAsync();
            await db.Users.Where(u => u.Id == f.Setter.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.DisplayName, "Renamed Setter"));
            db.WallChanges.Add(new WallChange
            {
                Kind = FeedKinds.UserProfile, EntityId = f.Setter.Id, Audience = FeedAudience.User, AudienceUserId = f.Setter.Id,
                ActorUserId = f.Setter.Id, SchemaVersion = FeedSchema.PayloadVersion,
            });
            await db.SaveChangesAsync();
        }

        var boulders = await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders);

        Assert.Equal("Renamed Setter", boulders.Boulders.Single(b => b.Id == f.PublishedId).SetBy);
    }

    [Fact]
    public async Task AWallDeletedInTheMiddleOfAHoldsBuild_IsNull_NotAThrow()
    {
        using var f = await SeededAsync();
        var interceptor = new DeleteWallOnSecondReadInterceptor(async () =>
        {
            await using var db = f.Harness.CreateContext();
            await db.Walls.Where(w => w.Id == f.OtherWallId).ExecuteDeleteAsync();
        });
        await using var reading = TestDb.Create(f.Harness.DbContextFactory.ConnectionString, interceptor);

        Assert.Null(await HoldsDocumentBuilder.BuildAsync(reading, f.OtherWallId, default));
    }

    [Fact]
    public void ALateBuildOfAnOldEpoch_NeverBecomesCurrent_AndACurrentEpochBuildReplacesAnOldOne()
    {
        var store = new DataDocumentStore();
        var wallId = Guid.NewGuid();
        var (old, now) = (Guid.NewGuid(), Guid.NewGuid());

        var fresh = Doc(wallId, "a", now, 5);
        store.Publish(fresh, now);
        store.Publish(Doc(wallId, "b", old, 900), now);
        Assert.Same(fresh, store.Current(wallId, DocumentAudience.Members, DataDocumentKinds.Holds));

        var other = new DataDocumentStore();
        other.Publish(Doc(wallId, "c", old, 900), old);
        var replacing = Doc(wallId, "d", now, 1);
        other.Publish(replacing, now);
        Assert.Same(replacing, other.Current(wallId, DocumentAudience.Members, DataDocumentKinds.Holds));
    }

    private static StoredDataDocument Doc(Guid wallId, string sha, Guid epoch, long seq)
    {
        var cursor = new FeedCursor(epoch, new FeedPosition(null, seq));
        return new StoredDataDocument(wallId, DocumentAudience.Members, DataDocumentKinds.Holds, sha, [1], [1], cursor, cursor, []);
    }

    private static async Task<DataDocumentFixture> SeededAsync()
    {
        var f = new DataDocumentFixture();
        await f.SeedAsync();
        return f;
    }
}
