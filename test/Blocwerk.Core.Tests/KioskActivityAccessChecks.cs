// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using NSubstitute;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The activity log and the comments as the wall's kiosk tablet reads them, shared by the SQLite and the PostgreSQL runs.
/// The tablet with nobody picked reads its own wall's activity under <see cref="KioskActivityVisibility"/> (no draft's or
/// archived boulder's entries, no membership or photo-staging entries) and no archived boulder's comments; acting as a
/// member it reads what that member reads; it never reads another wall; a share link's reads are unchanged.
/// </summary>
internal static class KioskActivityAccessChecks
{
    public static async Task WallActivityAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var log = await SeedLogAsync(h, s);
        var activity = s.Activity(viewer);

        if (DraftExposureScenario.UsesShareLink(viewer))
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => activity.GetWallActivityAsync(s.WallId));
            return;
        }

        var (items, total) = await activity.GetWallActivityAsync(s.WallId);

        var expected = viewer == DraftViewer.KioskAnonymous ? [log.Live, log.HoldAdded] : log.All;
        Assert.Equal(expected.Order(), items.Select(a => a.Id).Order());
        Assert.Equal(expected.Length, total);
    }

    public static async Task BoulderActivityAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var log = await SeedLogAsync(h, s);
        var activity = s.Activity(viewer);
        var token = s.TokenFor(viewer);

        var live = await activity.GetBoulderActivityAsync(s.LiveId, shareToken: token);
        Assert.Equal(log.Live, Assert.Single(live.Items).Id);

        if (DraftExposureScenario.SeesDrafts(viewer))
        {
            Assert.Single((await activity.GetBoulderActivityAsync(s.DraftId)).Items);
            Assert.Single((await activity.GetBoulderActivityAsync(log.ArchivedBoulder)).Items);
            return;
        }

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => activity.GetBoulderActivityAsync(s.DraftId, shareToken: token));

        // An archived boulder's log stays readable by its share link, as before; the tablet no longer lists it.
        if (viewer == DraftViewer.KioskAnonymous)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => activity.GetBoulderActivityAsync(log.ArchivedBoulder));
        }
        else
        {
            Assert.Single((await activity.GetBoulderActivityAsync(log.ArchivedBoulder, shareToken: token)).Items);
        }
    }

    /// <summary>
    /// The resting tablet asks for another wall it can name: refused, through the stamped context and through one that
    /// carries no kiosk stamp (the service's own wall check), and an anonymous visitor without a kiosk is refused too.
    /// </summary>
    public static async Task KioskStaysOnItsWallAsync(WallTestHarness h)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var otherBoulder = await CommentAccessChecks.SeedOtherWallAsync(h, s.Member.Id);
        Guid otherWall;
        await using (var db = h.CreateContext())
        {
            otherWall = db.Boulders.Single(b => b.Id == otherBoulder).WallId;
            db.ActivityLog.Add(new ActivityLogEntry { WallId = otherWall, BoulderId = otherBoulder, UserId = h.Owner.Id, Type = ActivityType.AttemptLogged });
            await db.SaveChangesAsync();
        }

        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(s.WallId);
        var unstamped = new ActivityLogService(h.DbContextFactory, Anonymous(), kiosk);
        var noKiosk = new ActivityLogService(h.DbContextFactory, Anonymous());

        foreach (var activity in new[] { s.Activity(DraftViewer.KioskAnonymous), unstamped })
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => activity.GetWallActivityAsync(otherWall));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => activity.GetBoulderActivityAsync(otherBoulder));
        }

        Assert.Equal(0, (await unstamped.GetWallActivityAsync(s.WallId)).TotalCount);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => noKiosk.GetWallActivityAsync(s.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => noKiosk.GetBoulderActivityAsync(s.LiveId));
    }

    /// <summary>The resting tablet reads a published boulder's comments, never an archived one's; a member reads both.</summary>
    public static async Task ArchivedCommentsAsync(WallTestHarness h)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var archived = await ArchivedBoulderAsync(h, s);
        await using (var db = h.CreateContext())
        {
            db.BoulderComments.Add(new BoulderComment { BoulderId = s.LiveId, UserId = s.Member.Id, Text = "live" });
            db.BoulderComments.Add(new BoulderComment { BoulderId = archived, UserId = s.Member.Id, Text = "old" });
            await db.SaveChangesAsync();
        }

        var kiosk = s.Comments(DraftViewer.KioskAnonymous);
        Assert.Equal(1, (await kiosk.GetCommentsAsync(s.LiveId)).TotalCount);
        Assert.Equal(0, (await kiosk.GetCommentsAsync(archived)).TotalCount);
        Assert.Equal(1, (await s.Comments(DraftViewer.KioskActAsMember).GetCommentsAsync(archived)).TotalCount);
        Assert.Equal(1, (await s.Comments(DraftViewer.ShareAnonymous).GetCommentsAsync(archived, shareToken: DraftExposureScenario.Token)).TotalCount);
    }

    private static async Task<(Guid ArchivedBoulder, Guid Live, Guid HoldAdded, Guid[] All)> SeedLogAsync(WallTestHarness h, DraftExposureScenario s)
    {
        var archived = await ArchivedBoulderAsync(h, s);
        await using var db = h.CreateContext();
        ActivityLogEntry Entry(Guid? boulder, ActivityType type)
        {
            var entry = new ActivityLogEntry { WallId = s.WallId, BoulderId = boulder, UserId = s.Member.Id, Type = type };
            db.ActivityLog.Add(entry);
            return entry;
        }

        var live = Entry(s.LiveId, ActivityType.AttemptLogged);
        var draft = Entry(s.DraftId, ActivityType.AttemptLogged);
        var old = Entry(archived, ActivityType.CommentAdded);
        var joined = Entry(null, ActivityType.MemberJoined);
        var staged = Entry(null, ActivityType.WallPhotoStaged);
        var hold = Entry(null, ActivityType.HoldAdded);
        await db.SaveChangesAsync();
        return (archived, live.Id, hold.Id, [live.Id, draft.Id, old.Id, joined.Id, staged.Id, hold.Id]);
    }

    private static async Task<Guid> ArchivedBoulderAsync(WallTestHarness h, DraftExposureScenario s)
    {
        await using var db = h.CreateContext();
        var boulder = new Boulder { WallId = s.WallId, Name = "Old One", CreatedByUserId = h.Owner.Id, IsArchived = true };
        db.Boulders.Add(boulder);
        await db.SaveChangesAsync();
        return boulder.Id;
    }

    private static ICurrentUserService Anonymous()
    {
        var users = Substitute.For<ICurrentUserService>();
        users.GetCurrentUserAsync().Returns(_ => Task.FromException<User>(new UnauthorizedAccessException()));
        return users;
    }
}
