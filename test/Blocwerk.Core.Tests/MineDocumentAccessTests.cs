// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Mine;
using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Whose marks <c>/api/me/walls/{w}/mine</c> reads (PR 3.4): only the caller's own, only on the wall asked for, only
/// for the boulders the caller's class sees; never on a kiosk tablet, for nobody, for a key, or for a share-link reader.
/// </summary>
public sealed class MineDocumentAccessTests
{
    [Fact]
    public async Task AMember_GetsTheirOwnTicksAndFavourites_OfThisWallOnly_DraftsAndArchivedIncluded()
    {
        using var f = await SeededAsync();

        var mine = await f.MineAsync();

        Assert.Equal(DataDocumentSchema.Version, mine.V);
        Assert.Equal(f.WallId, mine.WallId);
        var ticks = mine.Ticks.ToDictionary(t => t.BoulderId);
        Assert.Equal(new[] { f.Docs.PublishedId, f.Docs.DraftId, f.Docs.ArchivedId, f.Docs.HistoricId }.Order(), ticks.Keys.Order());
        Assert.Equal(mine.Ticks.Select(t => t.BoulderId).Order(), mine.Ticks.Select(t => t.BoulderId));
        var published = ticks[f.Docs.PublishedId];
        Assert.Equal(2, published.Attempts); // the seeded send and one attempt, never the setter's or the outsider's
        Assert.True(published.Sent);
        Assert.False(published.Flashed);
        Assert.True(ticks[f.Docs.DraftId].Flashed);
        Assert.False(ticks[f.Docs.ArchivedId].Sent);
        Assert.True(published.LastAt > new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero)); // the newest: the seeded send, now
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero), ticks[f.Docs.ArchivedId].LastAt);
        Assert.Equal(new[] { f.Docs.PublishedId, f.Docs.DraftId }.Order(), mine.Favourites);
        Assert.DoesNotContain(f.OtherWallBoulderId, ticks.Keys);
        Assert.DoesNotContain(f.OtherWallBoulderId, mine.Favourites);
    }

    [Fact]
    public async Task ASetter_GetsOnlyTheirOwn_NeverTheMembers()
    {
        using var f = await SeededAsync();
        f.Docs.Caller = f.Setter;

        var mine = await f.MineAsync();

        var tick = Assert.Single(mine.Ticks);
        Assert.Equal((f.Docs.PublishedId, 1, true), (tick.BoulderId, tick.Attempts, tick.Flashed));
        Assert.Equal([f.Docs.HistoricId], mine.Favourites);
    }

    [Fact]
    public async Task AKioskTablet_IsRefused_ActingAsAMember_OrResting_OnItsOwnWall()
    {
        using var f = await SeededAsync();
        f.Docs.BindKiosk(f.WallId);

        Assert.Equal(FeedAccessRefusal.NoAccess, (await f.AccessAsync()).Refusal);
        f.Docs.Caller = null; // a resting tablet: nobody acts
        Assert.Equal(FeedAccessRefusal.NoAccess, (await f.AccessAsync()).Refusal);
        Assert.Null((await f.AccessAsync()).UserId);
    }

    [Fact]
    public async Task Nobody_Is401_AGoneAccount410_AKey403_AndAnUnknownWall404()
    {
        using var f = await SeededAsync();

        f.Docs.Caller = null;
        Assert.Equal(FeedAccessRefusal.Unauthenticated, (await f.AccessAsync()).Refusal);

        f.Docs.Principal = FeedRouteFixture.SignedIn(); // authenticated, but the claims resolve to nobody: erased or merged
        Assert.Equal(FeedAccessRefusal.AccountGone, (await f.AccessAsync()).Refusal);

        f.Docs.Caller = f.Member;
        f.Docs.Principal = FeedRouteFixture.ApiKey();
        Assert.Equal(FeedAccessRefusal.NoAccess, (await f.AccessAsync()).Refusal);

        f.Docs.Principal = null;
        var unknown = await f.AccessAsync(Guid.NewGuid());
        Assert.True(unknown.WallMissing);
        Assert.Null(unknown.UserId);
    }

    /// <summary>A signed-in non-member: no class on the wall at all, and a share link would give the Outsider class.</summary>
    [Fact]
    public async Task ANonMember_IsRefused_AndTheOutsiderClassSeesNoMarks()
    {
        using var f = await SeededAsync();
        f.Docs.Caller = f.Outsider;

        var access = await f.AccessAsync();

        Assert.Equal(FeedAccessRefusal.NoAccess, access.Refusal);
        Assert.Null(access.UserId);
        Assert.False(DocumentAudiences.SeesMine(DocumentAudience.Outsider));
        Assert.False(DocumentAudiences.SeesMine(DocumentAudience.Kiosk));
        Assert.True(DocumentAudiences.SeesMine(DocumentAudience.Members));
        Assert.True(DocumentAudiences.SeesMine(DocumentAudience.Setters));
    }

    /// <summary>The marks cover the boulders of the class only: built for the Outsider class, no draft and no archived one.</summary>
    [Fact]
    public async Task TheBoulders_FollowTheClass_AnOutsiderBuildHasNoDraftOrArchivedMark()
    {
        using var f = await SeededAsync();

        var asOutsider = await f.Service().BuildAsync(f.WallId, f.Member.Id, DocumentAudience.Outsider, default);

        Assert.Equal(new[] { f.Docs.PublishedId, f.Docs.HistoricId }.Order(), asOutsider.Ticks.Select(t => t.BoulderId));
        Assert.Equal([f.Docs.PublishedId], asOutsider.Favourites);
    }

    /// <summary>The mark is read before the marks: a tick committed in between is in the document and replayed after it.</summary>
    [Fact]
    public async Task TheCursor_IsReadBeforeTheMarks()
    {
        using var f = await SeededAsync();
        var before = await f.Docs.Feed.CurrentAsync();
        var feed = new WriteAfterMarkFeed(f.Docs.Feed);
        f.Docs.Feed = feed;
        var raced = Guid.Empty;
        feed.Arm(async () =>
        {
            await using var db = f.Docs.Harness.CreateContext();
            var boulder = new Boulder { WallId = f.WallId, Name = "Raced", CreatedByUserId = f.Setter.Id };
            db.Boulders.Add(boulder);
            db.Attempts.Add(new Attempt { BoulderId = boulder.Id, UserId = f.Member.Id, Type = AttemptType.Send });
            await db.SaveChangesAsync();
            raced = boulder.Id;
        });

        var mine = await f.MineAsync();

        Assert.Equal(1, feed.Marks);
        Assert.NotEqual(Guid.Empty, raced);
        Assert.Contains(mine.Ticks, t => t.BoulderId == raced);
        Assert.Equal(before.ToString(), mine.Cursor);
    }

    private static async Task<MineDocumentFixture> SeededAsync()
    {
        var f = new MineDocumentFixture();
        await f.SeedAsync();
        return f;
    }
}
