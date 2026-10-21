// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The auth matrix of <c>GET /api/walls/{w}/changes</c> through the registered endpoint: each caller gets exactly the
/// audiences the hub would deliver to it, and each refusal its own status (401 / 403 / 410).
/// </summary>
public sealed class FeedRouteAccessTests
{
    [Fact]
    public async Task TheOwner_SeesWallMembersAndSetterRows_AndOnlyTheirOwnUserRows()
    {
        await using var f = await SeededAsync();

        Assert.Equal(["wall", "members", "setters", "owner.mine"], Kinds(await f.WallChangesAsync("?after=" + await f.BeginningAsync())));
    }

    [Fact]
    public async Task ASetterOrModerator_SeesDrafts_APlainMember_DoesNot()
    {
        await using var f = await SeededAsync();
        f.Caller = await f.Harness.AddMemberAsync("mod@test", WallRole.Moderator);
        Assert.Equal(["wall", "members", "setters"], Kinds(await f.WallChangesAsync("?after=" + await f.BeginningAsync())));

        f.Caller = f.Member;
        Assert.Equal(["wall", "members", "member.mine"], Kinds(await f.WallChangesAsync("?after=" + await f.BeginningAsync())));
    }

    /// <summary>PR 1.8's <c>panel.staged</c> is a Setters row: staged work never reaches members or share viewers.</summary>
    [Fact]
    public async Task APanelStagedRow_ReachesTheEditorsOnly()
    {
        await using var f = await SeededAsync();
        await f.CommitAsync(false, f.Row(FeedKinds.PanelStaged, FeedAudience.Setters));
        var after = await f.BeginningAsync();

        Assert.Contains(FeedKinds.PanelStaged, Kinds(await f.WallChangesAsync("?after=" + after)));
        f.Caller = f.Member;
        Assert.DoesNotContain(FeedKinds.PanelStaged, Kinds(await f.WallChangesAsync("?after=" + after)));
        f.Caller = null;
        Assert.DoesNotContain(FeedKinds.PanelStaged, Kinds(await f.WallChangesAsync($"?after={after}&token={FeedRouteFixture.ShareToken}")));
    }

    [Fact]
    public async Task ASignedInNonMember_IsRefused403_ButAShareTokenGrantsTheWallAudience()
    {
        await using var f = await SeededAsync();
        f.Caller = new User { Identifier = "nobody@test" };

        FeedRouteFixture.AssertRefused(await f.WallChangesAsync("?after=" + await f.BeginningAsync()), 403, FeedRefusals.NoAccess);
        Assert.Equal(["wall"], Kinds(await f.WallChangesAsync($"?after={await f.BeginningAsync()}&token={FeedRouteFixture.ShareToken}")));
    }

    [Fact]
    public async Task AnAnonymousShareViewer_GetsTheWallAudience_AStaleTokenIs403_NoTokenIs401()
    {
        await using var f = await SeededAsync();
        f.Caller = null;

        Assert.Equal(["wall"], Kinds(await f.WallChangesAsync($"?after={await f.BeginningAsync()}&token={FeedRouteFixture.ShareToken}")));
        FeedRouteFixture.AssertRefused(await f.WallChangesAsync($"?after={await f.BeginningAsync()}&token=other-token"), 403, FeedRefusals.NoAccess);
        FeedRouteFixture.AssertRefused(await f.WallChangesAsync("?after=" + await f.BeginningAsync()), 401, FeedRefusals.Unauthenticated);

        await using (var db = f.Harness.CreateContext())
        {
            await db.Walls.Where(w => w.Id == f.WallId).ExecuteUpdateAsync(u => u.SetProperty(w => w.ShareToken, "rotated"));
        }

        FeedRouteFixture.AssertRefused(
            await f.WallChangesAsync($"?after={await f.BeginningAsync()}&token={FeedRouteFixture.ShareToken}"), 403, FeedRefusals.NoAccess);
    }

    /// <summary>A tablet reads as <c>Kiosk()</c> whoever it acts as: the acting member's own rows never reach it.</summary>
    [Fact]
    public async Task AKiosk_ReadsItsOwnWallAsTheWallAudience_EvenActingAsAMember_AndNoOtherWall()
    {
        await using var f = await SeededAsync();
        f.BindKiosk(f.WallId);
        f.Caller = null;
        Assert.Equal(["wall"], Kinds(await f.WallChangesAsync("?after=" + await f.BeginningAsync())));

        f.Caller = f.Member;
        Assert.Equal(["wall"], Kinds(await f.WallChangesAsync("?after=" + await f.BeginningAsync())));
        FeedRouteFixture.AssertRefused(
            await f.WallChangesAsync($"?after={await f.BeginningAsync()}&token=other-token", f.OtherWallId), 403, FeedRefusals.NoAccess);
    }

    [Fact]
    public async Task AnApiKey_IsRefused_EvenForItsOwnersWall()
    {
        await using var f = await SeededAsync();
        f.Principal = FeedRouteFixture.ApiKey();

        var http = await f.WallChangesAsync("?after=" + await f.BeginningAsync());

        Assert.Equal(StatusCodes.Status404NotFound, http.Response.StatusCode); // DenyApiKeyPrincipals; the policy says 403 first
        await using var db = f.Harness.CreateContext();
        var gate = await FeedAccessGate.ForWallAsync(db, f.WallId, null, new FeedCaller(true, IsApiKey: true), f.CurrentUser, null, default);
        Assert.Equal(FeedAccessRefusal.NoAccess, gate.Refusal);
    }

    [Fact]
    public async Task ARemovedMember_Is403()
    {
        await using var f = await SeededAsync();
        f.Caller = f.Member;
        await using (var db = f.Harness.CreateContext())
        {
            await db.WallMembers.Where(m => m.UserId == f.Member.Id).ExecuteDeleteAsync();
        }

        FeedRouteFixture.AssertRefused(await f.WallChangesAsync("?after=" + await f.BeginningAsync()), 403, FeedRefusals.NoAccess);
    }

    /// <summary>The cookie still names somebody, but nobody who may sign in: the device must drop that account's data.</summary>
    [Fact]
    public async Task ASessionWhoseAccountIsGone_Is410()
    {
        await using var f = await SeededAsync();
        f.Caller = null;
        f.Principal = FeedRouteFixture.SignedIn();

        FeedRouteFixture.AssertRefused(await f.WallChangesAsync("?after=" + await f.BeginningAsync()), 410, FeedRefusals.AccountGone);
    }

    internal static async Task<FeedRouteFixture> SeededAsync(PostgresTestDatabase? postgres = null)
    {
        var f = new FeedRouteFixture(postgres);
        await f.StartAsync();
        await f.SeedAudiencesAsync();
        return f;
    }

    private static List<string> Kinds(HttpContext http) => FeedRouteFixture.Changes(http).Events.Select(e => e.Kind).ToList();
}
