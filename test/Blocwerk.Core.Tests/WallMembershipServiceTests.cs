// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.Membership;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Removing a member and leaving a wall: who may, what goes (the membership, private lists on this wall, an open
/// session, the home-wall choice; wall keys change hands), what stays (ticks, comments), and the invite token replaced so
/// the old link no longer lets anyone back in.
/// </summary>
public sealed class WallMembershipServiceTests
{
    [Fact]
    public async Task ARemovedMember_CannotRejoinWithTheOldInviteToken()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var oldToken = await h.WallService.GetOrCreateJoinTokenAsync(h.WallId);

        await Service(h).RemoveMemberAsync(h.WallId, member.Id);

        h.ActingUser = member;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.WallService.JoinWallAsync(oldToken));
        h.ActingUser = h.Owner;
        Assert.NotEqual(oldToken, await h.WallService.GetOrCreateJoinTokenAsync(h.WallId));
    }

    [Fact]
    public async Task Removal_EndsWhatIsPrivateOnThisWall_AndKeepsWhatOthersSee()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var boulder = await VisitFixture.SeedBoulderAsync(h, h.WallId);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var key = await SeedMemberDataAsync(h, member, boulder);

        await Service(h).RemoveMemberAsync(h.WallId, member.Id);

        await using var db = h.CreateContext();
        Assert.False(await db.WallMembers.AnyAsync(m => m.UserId == member.Id));
        Assert.False(await db.BoulderFavorites.AnyAsync(f => f.UserId == member.Id));
        Assert.False(await db.BoulderProjects.AnyAsync(p => p.UserId == member.Id));
        Assert.NotNull((await db.ClimbingSessions.SingleAsync(s => s.UserId == member.Id)).EndedAt);
        Assert.Null((await db.Users.SingleAsync(u => u.Id == member.Id)).HomeWallId);
        Assert.Equal(1, await db.Attempts.CountAsync(a => a.UserId == member.Id));
        Assert.Equal(1, await db.BoulderComments.CountAsync(c => c.UserId == member.Id));
        var handedOver = await db.ApiKeys.SingleAsync(k => k.Id == key);
        Assert.Equal(h.Owner.Id, handedOver.UserId);
        Assert.DoesNotContain("Ida", handedOver.Name);
    }

    [Fact]
    public async Task OnlyAnAdmin_Removes_NeverTheOwner_NeverThemselves()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var admin = await h.AddMemberAsync("admin@test", WallRole.Admin);

        h.ActingUser = member;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(h).RemoveMemberAsync(h.WallId, admin.Id));
        h.ActingUser = admin;
        Assert.Equal(MembershipRefusal.Owner, (await Assert.ThrowsAsync<MembershipRefusedException>(() => Service(h).RemoveMemberAsync(h.WallId, h.Owner.Id))).Reason);
        Assert.Equal(MembershipRefusal.Self, (await Assert.ThrowsAsync<MembershipRefusedException>(() => Service(h).RemoveMemberAsync(h.WallId, admin.Id))).Reason);
        Assert.Equal(MembershipRefusal.NotAMember, (await Assert.ThrowsAsync<MembershipRefusedException>(() => Service(h).RemoveMemberAsync(h.WallId, Guid.NewGuid()))).Reason);

        await Service(h).RemoveMemberAsync(h.WallId, member.Id);
    }

    [Fact]
    public async Task AMemberLeaves_TheOwnerCannot_AndAnOutsiderIsNotAMember()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Moderator);
        var oldToken = await h.WallService.GetOrCreateJoinTokenAsync(h.WallId);

        Assert.Equal(MembershipRefusal.Owner, (await Assert.ThrowsAsync<MembershipRefusedException>(() => Service(h).LeaveAsync(h.WallId, deleteBetaVideos: true))).Reason);
        h.ActingUser = member;
        await Service(h).LeaveAsync(h.WallId, deleteBetaVideos: true);
        Assert.Equal(MembershipRefusal.NotAMember, (await Assert.ThrowsAsync<MembershipRefusedException>(() => Service(h).LeaveAsync(h.WallId, deleteBetaVideos: true))).Reason);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.WallService.JoinWallAsync(oldToken));
    }

    [Fact]
    public async Task AKiosk_CanNeitherRemoveNorLeave()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        var service = new WallMembershipService(h.DbContextFactory, h.CurrentUser, h.ActivityLog, h.BetaStorage, NullLogger<WallMembershipService>.Instance, kiosk);

        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.RemoveMemberAsync(h.WallId, member.Id));
        h.ActingUser = member;
        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.LeaveAsync(h.WallId, deleteBetaVideos: true));
    }

    internal static WallMembershipService Service(WallTestHarness h) =>
        new(h.DbContextFactory, h.CurrentUser, h.ActivityLog, h.BetaStorage, NullLogger<WallMembershipService>.Instance);

    // A favourite, a project, a tick, a comment, an open session, the home wall and a wall key the member named.
    private static async Task<Guid> SeedMemberDataAsync(WallTestHarness h, User member, Guid boulder)
    {
        await using var db = h.CreateContext();
        db.BoulderFavorites.Add(new BoulderFavorite { BoulderId = boulder, UserId = member.Id });
        db.BoulderProjects.Add(new BoulderProject { BoulderId = boulder, UserId = member.Id });
        db.Attempts.Add(new Attempt { BoulderId = boulder, UserId = member.Id, Type = AttemptType.Send });
        db.BoulderComments.Add(new BoulderComment { BoulderId = boulder, UserId = member.Id, Text = "nice" });
        db.ClimbingSessions.Add(new ClimbingSession { UserId = member.Id, WallId = h.WallId });
        (await db.Users.SingleAsync(u => u.Id == member.Id)).HomeWallId = h.WallId;
        var key = new ApiKey { UserId = member.Id, WallId = h.WallId, Scope = ApiKeyScope.Wall, Name = "Ida's tablet", Prefix = "bwk_abcd", KeyHash = "x" };
        db.ApiKeys.Add(key);
        await db.SaveChangesAsync();
        return key.Id;
    }
}
