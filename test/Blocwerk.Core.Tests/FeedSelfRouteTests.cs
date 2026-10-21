// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Enums;
using Microsoft.AspNetCore.Http;

namespace Blocwerk.Core.Tests;

/// <summary>
/// <c>GET /api/me/changes</c>: the caller's own User rows across every wall (removals included, so a removed member's
/// devices learn it without the wall route), and nothing for a kiosk tablet, an API key or a session without an account.
/// </summary>
public sealed class FeedSelfRouteTests
{
    [Fact]
    public async Task AMember_GetsTheirOwnRows_OfEveryWall_AndNobodyElses()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var resync = f.Row(FeedKinds.UserResync, FeedAudience.User, f.Member.Id);
        resync.WallId = null;
        await f.CommitAsync(
            false,
            f.Row(FeedKinds.AccessRevoked, FeedAudience.User, f.Member.Id, wallId: f.OtherWallId, entityId: f.OtherWallId),
            resync,
            f.Row("stranger.mine", FeedAudience.User, Guid.NewGuid()));
        f.Caller = f.Member;

        var changes = FeedRouteFixture.Changes(await f.SelfChangesAsync("?after=" + await f.BeginningAsync()));

        Assert.Equal(["member.mine", FeedKinds.AccessRevoked, FeedKinds.UserResync], changes.Events.Select(e => e.Kind));
        Assert.Equal(f.OtherWallId, changes.Events[1].WallId);
        Assert.False(changes.Resync);
    }

    /// <summary>PR 1.8's <c>walls.changed</c> (added to a wall) is a User row: it reaches its user here, and nobody else.</summary>
    [Fact]
    public async Task AWallsChangedRow_ReachesItsUserOnly()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        await f.CommitAsync(
            false,
            f.Row(FeedKinds.WallsChanged, FeedAudience.User, f.Member.Id, wallId: f.OtherWallId, entityId: f.OtherWallId));
        var after = "?after=" + await f.BeginningAsync();

        f.Caller = f.Member;
        var mine = FeedRouteFixture.Changes(await f.SelfChangesAsync(after)).Events;
        f.Caller = f.Harness.Owner;
        var owners = FeedRouteFixture.Changes(await f.SelfChangesAsync(after)).Events;

        Assert.Equal(f.OtherWallId, Assert.Single(mine, e => e.Kind == FeedKinds.WallsChanged).EntityId);
        Assert.DoesNotContain(owners, e => e.Kind == FeedKinds.WallsChanged);
    }

    [Fact]
    public async Task WallLessRows_ComeBackWithNoWall()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var row = f.Row(FeedKinds.TicksResync, FeedAudience.User, f.Member.Id);
        row.WallId = null;
        await f.CommitAsync(false, row);
        f.Caller = f.Member;

        var events = FeedRouteFixture.Changes(await f.SelfChangesAsync("?after=" + await f.BeginningAsync())).Events;

        Assert.Null(Assert.Single(events, e => e.Kind == FeedKinds.TicksResync).WallId);
    }

    [Fact]
    public async Task Refusals_AreDistinct_401_403_410()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        var after = "?after=" + await f.BeginningAsync();

        f.Caller = null;
        FeedRouteFixture.AssertRefused(await f.SelfChangesAsync(after), StatusCodes.Status401Unauthorized, FeedRefusals.Unauthenticated);

        f.Principal = FeedRouteFixture.SignedIn();
        FeedRouteFixture.AssertRefused(await f.SelfChangesAsync(after), StatusCodes.Status410Gone, FeedRefusals.AccountGone);

        f.Principal = FeedRouteFixture.ApiKey();
        f.Caller = f.Member;
        FeedRouteFixture.AssertRefused(await f.SelfChangesAsync(after), StatusCodes.Status403Forbidden, FeedRefusals.NoAccess);
    }

    /// <summary>Per-user data is never stored on a kiosk (design §7.2), whoever the tablet acts as.</summary>
    [Fact]
    public async Task AKiosk_ActingAsAMember_Is403()
    {
        await using var f = await FeedRouteAccessTests.SeededAsync();
        f.BindKiosk(f.WallId);
        f.Caller = f.Member;

        FeedRouteFixture.AssertRefused(
            await f.SelfChangesAsync("?after=" + await f.BeginningAsync()), StatusCodes.Status403Forbidden, FeedRefusals.NoAccess);
    }
}
