using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Pins the member-facing invite path: <see cref="Services.IWallService.GetOrCreateShareTokenAsync"/>
/// lets any wall member hand out the wall's share link, rejects non-members, and — unlike the
/// admin-only regenerate — never replaces a token that already exists.
/// </summary>
public class WallInviteTokenTests
{
    [Fact]
    public async Task GetOrCreateShareToken_PlainMember_GetsToken()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        h.ActingUser = member;

        var token = await h.WallService.GetOrCreateShareTokenAsync(h.WallId);

        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public async Task GetOrCreateShareToken_OwnerWithoutMemberRow_GetsToken()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();

        // Legacy wall: the owner has no explicit WallMember row. The raw member check misses, but the
        // owner fallback must still hand them their own wall's link.
        await using (var db = h.CreateContext())
        {
            await db.WallMembers
                .Where(m => m.WallId == h.WallId && m.UserId == h.Owner.Id)
                .ExecuteDeleteAsync();
        }

        h.ActingUser = h.Owner;

        var token = await h.WallService.GetOrCreateShareTokenAsync(h.WallId);

        Assert.False(string.IsNullOrEmpty(token));
    }

    [Fact]
    public async Task GetOrCreateShareToken_NonMember_IsRejected()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        h.ActingUser = new User { Identifier = "stranger@test", DisplayName = "Stranger" };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => h.WallService.GetOrCreateShareTokenAsync(h.WallId));
    }

    [Fact]
    public async Task GetOrCreateShareToken_CalledTwice_ReturnsSameToken()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync();
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        h.ActingUser = member;

        var first = await h.WallService.GetOrCreateShareTokenAsync(h.WallId);
        var second = await h.WallService.GetOrCreateShareTokenAsync(h.WallId);

        Assert.Equal(first, second);
    }
}
