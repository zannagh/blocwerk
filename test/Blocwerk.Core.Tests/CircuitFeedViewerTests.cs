// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Web.State.Feed;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Whom a circuit subscribes as (PR 1.4): a kiosk tablet is always <see cref="FeedViewer.Kiosk"/> and only on its own wall,
/// even while it acts as a member; signed-in users get their role's audiences, with moderators among the Setters.
/// </summary>
public sealed class CircuitFeedViewerTests : IAsyncLifetime
{
    private readonly WallTestHarness harness = new();
    private readonly IKioskContext kiosk = Substitute.For<IKioskContext>();

    public async Task InitializeAsync() => await harness.SeedWallAsync(holdCount: 0);

    public Task DisposeAsync()
    {
        harness.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AKioskActingAsAMember_SubscribesAsTheKiosk_NeverAsThatMember()
    {
        var member = await harness.AddMemberAsync("sam@test", WallRole.Member);
        harness.ActingUser = member;
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(harness.WallId);

        var viewer = await Viewers().ResolveAsync(harness.WallId);

        Assert.NotNull(viewer);
        Assert.Null(viewer.UserId);
        Assert.Equal([FeedAudience.Wall], viewer.Audiences);
        Assert.False(viewer.Allows(new WallChange { Kind = FeedKinds.TickMine, Audience = FeedAudience.User, AudienceUserId = member.Id }));
        Assert.Null(await Viewers().ResolveAsync(Guid.NewGuid())); // another wall
    }

    [Fact]
    public async Task AKioskCircuitOnTheHub_GetsNoOwnRowsOfTheActingMember()
    {
        await using var f = new CircuitCacheFixture(viewers: h => new CircuitFeedViewer(kiosk, h.CurrentUser, h.RootContextFactory));
        await f.StartAsync();
        var member = await f.Harness.AddMemberAsync("kim@test", WallRole.Member);
        f.Harness.ActingUser = member;
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(f.WallId);
        await f.Link.EnsureAsync(f.WallId);

        await f.DeliverAsync(
            f.Row(FeedKinds.TickMine, audience: FeedAudience.User, user: member.Id),
            f.Row(FeedKinds.MemberChanged, audience: FeedAudience.Members),
            f.Row(FeedKinds.BoulderStats));

        Assert.Equal([FeedKinds.BoulderStats], f.Deliveries.SelectMany(d => d.Events).Select(e => e.Kind));
        Assert.Null(f.Link.ViewerOf(f.WallId)!.UserId);
    }

    [Theory]
    [InlineData(WallRole.Member, false)]
    [InlineData(WallRole.Moderator, true)]
    [InlineData(WallRole.Admin, true)]
    public async Task AMember_GetsTheirRolesAudiences(WallRole role, bool setters)
    {
        var user = await harness.AddMemberAsync($"{role}@test", role);
        harness.ActingUser = user;

        var viewer = (await Viewers().ResolveAsync(harness.WallId))!;

        Assert.Equal(user.Id, viewer.UserId);
        Assert.Contains(FeedAudience.Members, viewer.Audiences);
        Assert.Equal(setters, viewer.Audiences.Contains(FeedAudience.Setters));
    }

    [Fact]
    public async Task TheOwner_IsASetter_AndANonMember_GetsNothing()
    {
        Assert.True((await Viewers().ResolveAsync(harness.WallId))!.SameAs(FeedViewer.Setter(harness.Owner.Id)));

        harness.ActingUser = new User { Identifier = "stranger@test" };
        Assert.Null(await Viewers().ResolveAsync(harness.WallId));
    }

    [Theory]
    [InlineData("Member", false)]
    [InlineData("Moderator", true)]
    [InlineData("admin", true)]
    public void ARevokedRole_MapsToItsAudiences(string role, bool setters)
    {
        var viewer = FeedViewers.ForRoleName(role, Guid.NewGuid())!;

        Assert.Equal(setters, viewer.Audiences.Contains(FeedAudience.Setters));
        Assert.Null(FeedViewers.ForRoleName("Owner", Guid.NewGuid()));
    }

    private CircuitFeedViewer Viewers() => new(kiosk, harness.CurrentUser, harness.RootContextFactory);
}
