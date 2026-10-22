// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Assets.DataDocs;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The audience matrix of the data documents (Phase 3 plan §1, §3): each caller gets its own class, a sha is served only
/// from the caller's own class, and the index refuses with the feed's statuses.
/// </summary>
public sealed class DataDocumentAccessTests
{
    [Fact]
    public async Task TheSettersClass_CarriesTheDraft_TheMembersClass_DoesNot()
    {
        using var f = await SeededAsync();
        f.Caller = f.Setter;
        var setters = await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders);
        f.Caller = f.Member;
        var members = await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders);

        Assert.Equal(DocumentAudience.Members, (await f.AccessAsync()).Audience);
        Assert.Contains(setters.Boulders, b => b.Id == f.DraftId && b.Draft == true);
        Assert.Equal([f.PublishedId], members.Boulders.Select(b => b.Id));
        Assert.All(members.Boulders, b => Assert.Null(b.Draft));
    }

    [Fact]
    public async Task AMember_CannotFetchTheSettersSha_ButFetchesTheirOwn()
    {
        using var f = await SeededAsync();
        f.Caller = f.Setter;
        var settersSha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Boulders);
        f.Caller = f.Member;
        var membersSha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Boulders);

        Assert.NotEqual(settersSha, membersSha);
        Assert.Equal(StatusCodes.Status404NotFound, (await f.DocumentHttpAsync(DataDocumentKinds.Boulders, settersSha)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await f.DocumentHttpAsync(DataDocumentKinds.Boulders, membersSha)).Response.StatusCode);
    }

    /// <summary>The sha a setter was served stays in the store, but under the setters class, which they no longer are.</summary>
    [Fact]
    public async Task ADemotedSetter_CannotFetchTheSettersShaTheyWereServed()
    {
        using var f = await SeededAsync();
        f.Caller = f.Setter;
        var old = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Boulders);
        Assert.Equal(StatusCodes.Status200OK, (await f.DocumentHttpAsync(DataDocumentKinds.Boulders, old)).Response.StatusCode);
        await using (var db = f.Harness.CreateContext())
        {
            await db.WallMembers.Where(m => m.UserId == f.Setter.Id && m.WallId == f.WallId)
                .ExecuteUpdateAsync(u => u.SetProperty(m => m.Role, WallRole.Member));
        }

        Assert.Equal(DocumentAudience.Members, (await f.AccessAsync()).Audience);
        Assert.Equal(StatusCodes.Status404NotFound, (await f.DocumentHttpAsync(DataDocumentKinds.Boulders, old)).Response.StatusCode);
        Assert.DoesNotContain((await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders)).Boulders, b => b.Id == f.DraftId);
    }

    [Fact]
    public async Task AShareLink_ReadsTheOutsiderClass_AStaleTokenIs403_NoTokenIs401_ARotatedTokenLosesItsSha()
    {
        using var f = await SeededAsync();
        f.Caller = null;
        Assert.Equal(DocumentAudience.Outsider, (await f.AccessAsync(DataDocumentFixture.ShareToken)).Audience);
        var index = await f.IndexAsync(DataDocumentFixture.ShareToken);
        var sha = DataDocumentFixture.Sha(index, DataDocumentKinds.Boulders);
        Assert.Equal([f.PublishedId], (await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders, DataDocumentFixture.ShareToken)).Boulders.Select(b => b.Id));

        FeedRouteFixture.AssertRefused(await f.IndexHttpAsync("stale-token"), 403, FeedRefusals.NoAccess);
        FeedRouteFixture.AssertRefused(await f.IndexHttpAsync(), 401, FeedRefusals.Unauthenticated);
        Assert.Equal(StatusCodes.Status404NotFound, (await f.DocumentHttpAsync(DataDocumentKinds.Boulders, sha, "stale-token")).Response.StatusCode);

        await using (var db = f.Harness.CreateContext())
        {
            await db.Walls.Where(w => w.Id == f.WallId).ExecuteUpdateAsync(u => u.SetProperty(w => w.ShareToken, "rotated"));
        }

        FeedRouteFixture.AssertRefused(await f.IndexHttpAsync(DataDocumentFixture.ShareToken), 403, FeedRefusals.NoAccess);
        Assert.Equal(StatusCodes.Status404NotFound,
            (await f.DocumentHttpAsync(DataDocumentKinds.Boulders, sha, DataDocumentFixture.ShareToken)).Response.StatusCode);
        Assert.Equal(StatusCodes.Status200OK, (await f.DocumentHttpAsync(DataDocumentKinds.Boulders, sha, "rotated")).Response.StatusCode);
    }

    /// <summary>Membership wins over a token; a signed-in non-member with the token reads as an outsider.</summary>
    [Fact]
    public async Task ASignedInNonMember_WithTheToken_ReadsTheOutsiderClass_WithoutIt403()
    {
        using var f = await SeededAsync();
        f.Caller = new User { Identifier = "stranger@test" };

        Assert.Equal(DocumentAudience.Outsider, (await f.AccessAsync(DataDocumentFixture.ShareToken)).Audience);
        FeedRouteFixture.AssertRefused(await f.IndexHttpAsync(), 403, FeedRefusals.NoAccess);
    }

    [Fact]
    public async Task AKiosk_ReadsTheKioskClassOfItsBoundWall_AndNoOtherWall()
    {
        using var f = await SeededAsync();
        f.Caller = null;
        f.BindKiosk(f.WallId);
        Assert.Equal(DocumentAudience.Kiosk, (await f.AccessAsync()).Audience);
        var sha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Holds);
        Assert.Equal(StatusCodes.Status200OK, (await f.DocumentHttpAsync(DataDocumentKinds.Holds, sha)).Response.StatusCode);

        FeedRouteFixture.AssertRefused(await f.IndexHttpAsync(wallId: f.OtherWallId), 403, FeedRefusals.NoAccess);
        FeedRouteFixture.AssertRefused(await f.IndexHttpAsync("other-token", f.OtherWallId), 403, FeedRefusals.NoAccess);
        Assert.Equal(StatusCodes.Status404NotFound,
            (await f.DocumentHttpAsync(DataDocumentKinds.Holds, sha, wallId: f.OtherWallId)).Response.StatusCode);
    }

    /// <summary>Kiosk first: a tablet acting as a setter is the kiosk class, so it never gets a draft or the setters' sha.</summary>
    [Fact]
    public async Task AnActAsSetterOnAKiosk_GetsTheKioskClass_NoDrafts()
    {
        using var f = await SeededAsync();
        f.Caller = f.Setter;
        var settersSha = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Boulders);
        f.BindKiosk(f.WallId);

        Assert.Equal(DocumentAudience.Kiosk, (await f.AccessAsync()).Audience);
        var kiosk = await f.DocumentAsync<BouldersDocument>(DataDocumentKinds.Boulders);
        Assert.Equal([f.PublishedId], kiosk.Boulders.Select(b => b.Id));
        Assert.All(kiosk.Boulders, b => Assert.Null(b.Draft));
        Assert.Equal(StatusCodes.Status404NotFound, (await f.DocumentHttpAsync(DataDocumentKinds.Boulders, settersSha)).Response.StatusCode);
    }

    [Fact]
    public async Task AKioskWhoseKeyIsNoLongerLive_Is403()
    {
        using var f = await SeededAsync();
        f.Caller = null;
        f.BindKiosk(f.WallId);
        f.KioskKeys.IsKeyValidAsync(f.KioskKeyId, f.WallId, Arg.Any<CancellationToken>()).Returns(false);

        FeedRouteFixture.AssertRefused(await f.IndexHttpAsync(), 403, FeedRefusals.NoAccess);
    }

    [Fact]
    public async Task AnUnknownWall_Is404_AGoneAccount_Is410()
    {
        using var f = await SeededAsync();
        var unknown = await f.IndexHttpAsync(wallId: Guid.NewGuid());
        Assert.Equal(StatusCodes.Status404NotFound, unknown.Response.StatusCode);
        Assert.Equal("no-store", unknown.Response.Headers.CacheControl.ToString());

        f.Caller = null;
        f.Principal = FeedRouteFixture.SignedIn();
        FeedRouteFixture.AssertRefused(await f.IndexHttpAsync(), 410, FeedRefusals.AccountGone);
    }

    /// <summary>A kept-unconfirmed hold is marked in every class's holds document, and the mark is part of the sha.</summary>
    [Fact]
    public async Task AKeptHold_IsMarkedInEveryClass_AndMovesTheHoldsSha()
    {
        using var f = await SeededAsync();
        var before = DataDocumentFixture.Sha(await f.IndexAsync(), DataDocumentKinds.Holds);
        Guid keptId;
        await using (var db = f.Harness.CreateContext())
        {
            var hold = await db.Holds.OrderBy(h => h.X).FirstAsync(h => h.WallId == f.WallId);
            hold.KeptUnconfirmedGeneration = 0;
            keptId = hold.Id;
            await db.SaveChangesAsync();
        }

        await f.AddFeedRowAsync(FeedKinds.HoldChanged);
        var setters = await f.DocumentAsync<HoldsDocument>(DataDocumentKinds.Holds);
        f.Caller = null;
        var outsider = await f.DocumentAsync<HoldsDocument>(DataDocumentKinds.Holds, DataDocumentFixture.ShareToken);

        Assert.NotEqual(before, DataDocumentFixture.Sha(await f.IndexAsync(DataDocumentFixture.ShareToken), DataDocumentKinds.Holds));
        foreach (var doc in new[] { setters, outsider })
        {
            Assert.Equal([keptId], doc.Holds.Where(h => h.Kept).Select(h => h.Id));
            Assert.Equal(3, doc.Holds.Count);
        }
    }

    private static async Task<DataDocumentFixture> SeededAsync()
    {
        var f = new DataDocumentFixture();
        await f.SeedAsync();
        return f;
    }
}
