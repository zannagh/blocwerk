// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Feed;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// An attempt's activity entry ("sent this") lives and dies with the attempt: written in the attempt's save with its id,
/// deleted in the delete's save, and announced as <c>activity.removed</c> to the lists that show it (members, the kiosk),
/// live and on catch-up, never to a share link. A deleted boulder takes its attempts' entries with it.
/// </summary>
public sealed class AttemptActivityRemovalTests : IAsyncLifetime
{
    private readonly InProcessFeedSignal signal = new();
    private readonly SocialFeedFixture fixture;
    private readonly WallFeed feed;
    private readonly WallFeedHub hub;
    private readonly FeedCatchUp catchUp;

    public AttemptActivityRemovalTests()
    {
        fixture = new SocialFeedFixture(signal: signal);
        var connectionString = fixture.Harness.DbContextFactory.ConnectionString;
        feed = new WallFeed(() => TestDb.Create(connectionString), NullLogger<WallFeed>.Instance);
        hub = new WallFeedHub(feed, signal, () => TestDb.Create(connectionString), NullLogger<WallFeedHub>.Instance);
        catchUp = new FeedCatchUp(feed, () => TestDb.Create(connectionString));
    }

    public async Task InitializeAsync() => await hub.StartAsync(CancellationToken.None);

    public async Task DisposeAsync()
    {
        await hub.StopAsync(CancellationToken.None);
        hub.Dispose();
        fixture.Dispose();
    }

    [Fact]
    public async Task DeletingAnAttempt_RemovesItsEntry_AndTellsMembersAndTheKiosk_LiveAndOnCatchUp_NeverAShareLink()
    {
        var boulder = await fixture.SeedAsync();
        var climber = await fixture.Harness.AddMemberAsync("climber@test", WallRole.Member);
        var member = await SubscribeAsync(FeedViewer.Member(fixture.OwnerId));
        var kiosk = await SubscribeAsync(FeedViewer.Kiosk());
        var share = await SubscribeAsync(FeedViewer.Anonymous());
        var start = (await feed.CurrentAsync()).ToString();
        var attempts = Attempts(climber);

        var send = await attempts.LogAttemptAsync(boulder, AttemptType.Send);
        var entry = await SingleEntryAsync(boulder);
        Assert.Equal((send.Id, "Send"), (entry.AttemptId, entry.Details));

        await attempts.DeleteAttemptAsync(send.Id);

        Assert.Equal(0, await EntriesAsync(boulder));
        foreach (var viewer in new[] { member, kiosk })
        {
            await UntilAsync(viewer, e => e.Kind == FeedKinds.ActivityRemoved && e.EntityId == entry.Id && e.BoulderId == boulder);
        }

        // The share link got the attempt's stats rows, so everything before them reached it: no activity among them.
        await UntilAsync(share, e => e.Kind == FeedKinds.BoulderStats);
        await UntilAsync(member, e => e.Kind == FeedKinds.BoulderStats);
        Assert.DoesNotContain(share.Events, e => e.Kind is FeedKinds.ActivityAdded or FeedKinds.ActivityRemoved);
        Assert.Contains(FeedKinds.ActivityRemoved, await CaughtUpAsync(FeedViewer.Kiosk(), start));
        Assert.DoesNotContain(FeedKinds.ActivityRemoved, await CaughtUpAsync(FeedViewer.Anonymous(), start));

        // On reload: gone from the member's list and from the resting tablet's.
        Assert.Equal(0, (await new ActivityLogService(fixture.Harness.DbContextFactory, Users(climber)).GetBoulderActivityAsync(boulder)).TotalCount);
        Assert.Equal(0, (await RestingKioskActivity().GetBoulderActivityAsync(boulder)).TotalCount);
    }

    [Fact]
    public async Task TheKiosk_HearsOfNoRemovalOnADraft_AMemberDoes()
    {
        var boulder = await fixture.SeedAsync();
        var climber = await fixture.Harness.AddMemberAsync("climber@test", WallRole.Member);
        var send = await Attempts(climber).LogAttemptAsync(boulder, AttemptType.Send);
        await using (var db = fixture.Harness.CreateContext())
        {
            await db.Boulders.Where(b => b.Id == boulder).ExecuteUpdateAsync(u => u.SetProperty(b => b.IsDraft, true));
        }

        var start = (await feed.CurrentAsync()).ToString();
        await Attempts(climber).DeleteAttemptAsync(send.Id);

        Assert.DoesNotContain(FeedKinds.ActivityRemoved, await CaughtUpAsync(FeedViewer.Kiosk(), start));
        Assert.Contains(FeedKinds.ActivityRemoved, await CaughtUpAsync(FeedViewer.Member(fixture.OwnerId), start));
    }

    [Fact]
    public async Task DeletingTheBoulder_TakesItsAttemptsEntriesWithIt()
    {
        var boulder = await fixture.SeedAsync();
        await Attempts(fixture.Harness.Owner).LogAttemptAsync(boulder, AttemptType.Flash);
        Assert.Equal(1, await EntriesAsync(boulder));

        await using (var db = fixture.Harness.CreateContext())
        {
            db.Boulders.Remove(await db.Boulders.SingleAsync(b => b.Id == boulder));
            await db.SaveChangesAsync();
        }

        await using var check = fixture.Harness.CreateContext();
        Assert.False(await check.ActivityLog.AnyAsync(a => a.Type == ActivityType.AttemptLogged));
    }

    private static ICurrentUserService Users(User user)
    {
        var users = Substitute.For<ICurrentUserService>();
        users.GetCurrentUserAsync().Returns(Task.FromResult(user));
        return users;
    }

    private AttemptService Attempts(User user) =>
        new(new FeedDbContextFactory(fixture.Harness, signal), Users(user), NullLogger<AttemptService>.Instance);

    private ActivityLogService RestingKioskActivity()
    {
        var nobody = Substitute.For<ICurrentUserService>();
        nobody.GetCurrentUserAsync().Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(fixture.WallId);
        return new ActivityLogService(new KioskStampingFactory(fixture.Harness.DbContextFactory, kiosk), nobody, kiosk);
    }

    private async Task<ActivityLogEntry> SingleEntryAsync(Guid boulder)
    {
        await using var db = fixture.Harness.CreateContext();
        return await db.ActivityLog.AsNoTracking().SingleAsync(a => a.BoulderId == boulder && a.Type == ActivityType.AttemptLogged);
    }

    private async Task<int> EntriesAsync(Guid boulder)
    {
        await using var db = fixture.Harness.CreateContext();
        return await db.ActivityLog.CountAsync(a => a.BoulderId == boulder && a.Type == ActivityType.AttemptLogged);
    }

    private async Task<List<string>> CaughtUpAsync(FeedViewer viewer, string after) =>
        (await catchUp.ReadWallAsync(fixture.WallId, viewer, after, null)).Events.Select(e => e.Kind).ToList();

    private static async Task UntilAsync(RecordingFeedSubscriber viewer, Func<FeedEnvelope, bool> match)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!viewer.Events.Any(match))
        {
            Assert.True(DateTime.UtcNow < deadline, "the event never arrived");
            await Task.Delay(20);
        }
    }

    private async Task<RecordingFeedSubscriber> SubscribeAsync(FeedViewer viewer)
    {
        var subscriber = new RecordingFeedSubscriber();
        await hub.SubscribeAsync(fixture.WallId, viewer, subscriber);
        return subscriber;
    }
}
