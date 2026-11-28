// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Services.Membership;
using Blocwerk.Core.Visits;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What ending a membership does beyond the membership row: the person's visitor links and the visits made with them
/// end, the invite is replaced while the public share link stays, their beta clips go (on removal, and on leaving unless
/// they keep them), a legacy owner can remove, and the wall's log says who was removed and who left.
/// </summary>
public sealed class WallMembershipEndTests
{
    [Fact]
    public async Task Removal_DeletesTheirVisitorLinks_AndEndsAVisitAlreadyUnderWay()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        h.ActingUser = member;
        var clock = new VisitTestClock(DateTimeOffset.UtcNow);
        var used = await VisitFixture.Service(h, clock).CreateAsync(h.WallId);
        var unused = await VisitFixture.Service(h, clock).CreateAsync(h.WallId);
        var grant = await VisitFixture.Service(h, clock).RedeemAsync(used.Token, Guid.NewGuid());
        var check = new VisitGrantCheck(h.RootContextFactory);
        Assert.True(await check.IsLiveAsync(grant!));

        h.ActingUser = h.Owner;
        await WallMembershipServiceTests.Service(h).RemoveMemberAsync(h.WallId, member.Id);

        await using var db = h.CreateContext();
        Assert.False(await db.WallVisitLinks.AnyAsync(l => l.CreatedByUserId == member.Id));
        Assert.False(await check.IsLiveAsync(grant!));
        Assert.Equal(VisitLinkStatus.NotFound, (await VisitFixture.Service(h, clock).LookUpAsync(unused.Token)).Status);
    }

    [Fact]
    public async Task ALinkWhoseMakerIsNoLongerOnTheWall_IsNeitherRedeemedNorLive()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        h.ActingUser = member;
        var clock = new VisitTestClock(DateTimeOffset.UtcNow);
        var open = await VisitFixture.Service(h, clock).CreateAsync(h.WallId);
        var used = await VisitFixture.Service(h, clock).CreateAsync(h.WallId);
        var grant = await VisitFixture.Service(h, clock).RedeemAsync(used.Token, Guid.NewGuid());

        // The member row gone by another way than the service (which would delete the links too).
        await using (var db = h.CreateContext())
        {
            await db.WallMembers.Where(m => m.UserId == member.Id).ExecuteDeleteAsync();
        }

        VisitFixture.GoAnonymous(h);
        Assert.Null(await VisitFixture.Service(h, clock).RedeemAsync(open.Token, Guid.NewGuid()));
        Assert.False(await new VisitGrantCheck(h.RootContextFactory).IsLiveAsync(grant!));
    }

    [Fact]
    public async Task AfterARemoval_TheOldInviteFails_TheShareLinkStillShowsTheWall_AndTheShareTokenNeverJoins()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        var stranger = await h.AddMemberAsync("stranger@test", WallRole.Member);
        var share = await WallShareTokens.MintAsync(h);
        var invite = await h.WallService.GetOrCreateJoinTokenAsync(h.WallId);
        Assert.NotEqual(share, invite);

        await WallMembershipServiceTests.Service(h).RemoveMemberAsync(h.WallId, member.Id);

        Assert.Equal(h.WallId, (await h.WallService.GetWallByShareTokenAsync(share))?.Id);
        Assert.Equal(share, await WallShareTokens.MintAsync(h));
        h.ActingUser = member;
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.WallService.JoinWallAsync(invite));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.WallService.JoinWallAsync(share));
        await using var db = h.CreateContext();
        Assert.False(await db.WallMembers.AnyAsync(m => m.UserId == member.Id));
        Assert.True(await db.WallMembers.AnyAsync(m => m.UserId == stranger.Id));
    }

    [Fact]
    public async Task RotatingTheInvite_KeepsTheShareLink_AndAShareViewerIsNotHandedTheInvite()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var share = await WallShareTokens.MintAsync(h);
        var invite = await h.WallService.GetOrCreateJoinTokenAsync(h.WallId);

        var rotated = await h.WallService.RotateJoinTokenAsync(h.WallId);

        Assert.NotEqual(invite, rotated);
        Assert.Equal(share, await WallShareTokens.MintAsync(h));
        var shared = await h.WallService.GetWallByShareTokenAsync(share);
        Assert.Null(shared!.JoinToken);
        Assert.Equal(h.WallId, (await h.WallService.GetWallByJoinTokenAsync(rotated))?.Id);
        Assert.Null(await h.WallService.GetWallByJoinTokenAsync(invite));
    }

    [Fact]
    public async Task Removal_DeletesTheirClipsOnThisWall_AndLeavingKeepsThemWhenAsked()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var boulder = await VisitFixture.SeedBoulderAsync(h, h.WallId);
        var otherBoulder = await VisitFixture.SeedBoulderAsync(h, await VisitFixture.SeedOtherWallAsync(h));
        var removed = await h.AddMemberAsync("removed@test", WallRole.Member);
        var leaver = await h.AddMemberAsync("leaver@test", WallRole.Member);
        var removedClip = await SeedClipAsync(h, boulder, removed, "removed.mp4");
        var elsewhere = await SeedClipAsync(h, otherBoulder, removed, "elsewhere.mp4");
        var kept = await SeedClipAsync(h, boulder, leaver, "kept.mp4");
        var storage = Substitute.For<IBetaVideoStorage>();

        await Service(h, storage).RemoveMemberAsync(h.WallId, removed.Id);
        h.ActingUser = leaver;
        await Service(h, storage).LeaveAsync(h.WallId, deleteBetaVideos: false);

        await using var db = h.CreateContext();
        var left = await db.BetaVideos.IgnoreQueryFilters().Select(v => v.Id).ToListAsync();
        Assert.Equal(new[] { elsewhere, kept }.Order(), left.Order());
        storage.Received(1).Delete("removed.mp4");
        storage.Received(1).DeleteHlsDirectory(removedClip);
        storage.DidNotReceive().Delete("kept.mp4");
    }

    [Fact]
    public async Task Leaving_DeletesTheirClipsByDefault()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var boulder = await VisitFixture.SeedBoulderAsync(h, h.WallId);
        var leaver = await h.AddMemberAsync("leaver@test", WallRole.Member);
        await SeedClipAsync(h, boulder, leaver, "mine.mp4");
        var storage = Substitute.For<IBetaVideoStorage>();

        h.ActingUser = leaver;
        await Service(h, storage).LeaveAsync(h.WallId, deleteBetaVideos: true);

        await using var db = h.CreateContext();
        Assert.False(await db.BetaVideos.IgnoreQueryFilters().AnyAsync());
        storage.Received(1).Delete("mine.mp4");
    }

    [Fact]
    public async Task AnOwnerWithNoMemberRow_RemovesAMember_AndStillCannotLeave()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var member = await h.AddMemberAsync("member@test", WallRole.Member);
        await using (var db = h.CreateContext())
        {
            await db.WallMembers.Where(m => m.UserId == h.Owner.Id).ExecuteDeleteAsync();
        }

        await WallMembershipServiceTests.Service(h).RemoveMemberAsync(h.WallId, member.Id);

        var refusal = await Assert.ThrowsAsync<MembershipRefusedException>(() => WallMembershipServiceTests.Service(h).LeaveAsync(h.WallId, true));
        Assert.Equal(MembershipRefusal.Owner, refusal.Reason);
        await using var after = h.CreateContext();
        Assert.False(await after.WallMembers.IgnoreQueryFilters().AnyAsync(m => m.UserId == member.Id));
    }

    [Fact]
    public async Task TheLog_SaysWhoWasRemovedAndWhoLeft()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 0);
        var removed = await h.AddMemberAsync("removed@test", WallRole.Member);
        var leaver = await h.AddMemberAsync("leaver@test", WallRole.Member);
        var log = new ActivityLogService(h.DbContextFactory, h.CurrentUser);
        var service = new WallMembershipService(h.DbContextFactory, h.CurrentUser, log, h.BetaStorage, NullLogger<WallMembershipService>.Instance);

        await service.RemoveMemberAsync(h.WallId, removed.Id);
        h.ActingUser = leaver;
        await service.LeaveAsync(h.WallId, true);

        h.ActingUser = h.Owner;
        var (items, _) = await log.GetWallActivityAsync(h.WallId);
        var removal = Assert.Single(items, i => i.Type == ActivityType.MemberRemoved);
        Assert.Equal((h.Owner.Id, removed.Id), (removal.UserId, removal.SubjectUserId));
        Assert.Equal(removed.Name, removal.SubjectUser?.Name);
        var left = Assert.Single(items, i => i.Type == ActivityType.MemberLeft);
        Assert.Equal((leaver.Id, (Guid?)leaver.Id), (left.UserId, left.SubjectUserId));
    }

    private static WallMembershipService Service(WallTestHarness h, IBetaVideoStorage storage) =>
        new(h.DbContextFactory, h.CurrentUser, h.ActivityLog, storage, NullLogger<WallMembershipService>.Instance);

    private static async Task<Guid> SeedClipAsync(WallTestHarness h, Guid boulder, User uploader, string path)
    {
        await using var db = h.CreateContext();
        var clip = new BetaVideo { BoulderId = boulder, UploadedByUserId = uploader.Id, ContentType = "video/mp4", StoragePath = path, SizeBytes = 10 };
        db.BetaVideos.Add(clip);
        await db.SaveChangesAsync();
        return clip.Id;
    }
}
