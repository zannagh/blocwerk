// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Reads keyed by a wall id over tables without a query filter of their own (members, setters, the member boulder
/// list), per viewer, shared by the SQLite and the PostgreSQL runs: only someone who can see the wall gets anything,
/// a kiosk acting as a member only on the tablet's wall, and drafts' setters only for members.
/// </summary>
internal static class WallMemberAccessChecks
{
    public static async Task MembersAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var walls = s.Walls(viewer);

        if (DraftExposureScenario.SeesDrafts(viewer))
        {
            var members = await walls.GetMembersAsync(s.WallId);
            Assert.Equal(new HashSet<Guid> { h.Owner.Id, s.Member.Id }, members.Select(m => m.UserId).ToHashSet());
        }
        else if (viewer == DraftViewer.ShareNonMember)
        {
            Assert.Empty(await walls.GetMembersAsync(s.WallId));
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => walls.GetMembersAsync(s.WallId));
        }
    }

    public static async Task SetterNamesAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        await SeedSettersAsync(h, s);
        var names = await s.Walls(viewer).GetBoulderSetterNamesAsync(s.WallId, s.TokenFor(viewer));

        // Every viewer here can see the wall (the share link, the kiosk on its wall, a member), so the live boulder's
        // setter is named for all; the draft's only for a member.
        Assert.Equal(["member@test"], names[s.LiveId]);
        Assert.Equal(DraftExposureScenario.SeesDrafts(viewer), names.ContainsKey(s.DraftId));
    }

    /// <summary>A stranger with no link and a wrong token get nothing; an anonymous visitor is sent to sign in.</summary>
    public static async Task SetterNamesForStrangersAsync(WallTestHarness h)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        await SeedSettersAsync(h, s);

        Assert.Empty(await s.Walls(DraftViewer.ShareNonMember).GetBoulderSetterNamesAsync(s.WallId));
        Assert.Empty(await s.Walls(DraftViewer.ShareAnonymous).GetBoulderSetterNamesAsync(s.WallId, "not-the-token"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => s.Walls(DraftViewer.ShareAnonymous).GetBoulderSetterNamesAsync(s.WallId));
    }

    public static async Task BoulderListAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var feedback = s.Feedback(viewer);

        if (DraftExposureScenario.SeesDrafts(viewer))
        {
            var ids = (await feedback.GetBoulderListAsync(s.WallId)).Select(i => i.Boulder.Id).ToHashSet();
            Assert.Equal(new HashSet<Guid> { s.LiveId, s.DraftId }, ids);
        }
        else if (viewer == DraftViewer.ShareNonMember)
        {
            Assert.Empty(await feedback.GetBoulderListAsync(s.WallId));
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => feedback.GetBoulderListAsync(s.WallId));
        }
    }

    /// <summary>The member is on a second wall too: their own session reads it, the tablet acting as them does not.</summary>
    public static async Task KioskActAsStaysOnItsWallAsync(WallTestHarness h)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var otherWall = await OtherWallIdAsync(h, await CommentAccessChecks.SeedOtherWallAsync(h, s.Member.Id));

        var kiosk = DraftViewer.KioskActAsMember;
        Assert.Empty(await s.Walls(kiosk).GetMembersAsync(otherWall));
        Assert.Empty(await s.Walls(kiosk).GetBoulderSetterNamesAsync(otherWall));
        Assert.Empty(await s.Feedback(kiosk).GetBoulderListAsync(otherWall));

        Assert.Equal(2, (await s.Walls(DraftViewer.Member).GetMembersAsync(otherWall)).Count);
        Assert.Single(await s.Feedback(DraftViewer.Member).GetBoulderListAsync(otherWall));
    }

    private static async Task SeedSettersAsync(WallTestHarness h, DraftExposureScenario s)
    {
        await using var db = h.CreateContext();
        db.BoulderSetters.Add(new BoulderSetter { BoulderId = s.LiveId, UserId = s.Member.Id });
        db.BoulderSetters.Add(new BoulderSetter { BoulderId = s.DraftId, UserId = s.Member.Id });
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> OtherWallIdAsync(WallTestHarness h, Guid boulderId)
    {
        await using var db = h.CreateContext();
        return await db.Boulders.Where(b => b.Id == boulderId).Select(b => b.WallId).SingleAsync();
    }
}
