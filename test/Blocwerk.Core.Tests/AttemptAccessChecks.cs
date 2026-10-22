// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Ticks per viewer, shared by the SQLite and the PostgreSQL runs: logged, listed and deleted by the wall's members only
/// (a kiosk acting as a member counts, on the tablet's wall), with a non-member refused exactly like a missing boulder.
/// Also the membership answer the share route's buttons follow.
/// </summary>
internal static class AttemptAccessChecks
{
    public static async Task LogAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var attempts = s.Attempts(viewer);

        if (DraftExposureScenario.SeesDrafts(viewer))
        {
            Assert.Equal(s.Member.Id, (await attempts.LogAttemptAsync(s.LiveId, AttemptType.Send)).UserId);

            // A member still cannot tick a draft; that rule is unchanged.
            var draft = await Assert.ThrowsAsync<InvalidOperationException>(() => attempts.LogAttemptAsync(s.DraftId, AttemptType.Send));
            Assert.Equal(AttemptService.DraftNotLoggableMessage, draft.Message);
            Assert.Equal(1, await CountAsync(h));
            return;
        }

        if (viewer == DraftViewer.ShareNonMember)
        {
            // The same answer for the live boulder, the draft and a boulder that does not exist: no existence oracle.
            foreach (var id in new[] { s.LiveId, s.DraftId, Guid.NewGuid() })
            {
                var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => attempts.LogAttemptAsync(id, AttemptType.Send));
                Assert.Equal("Boulder not found", ex.Message);
            }
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => attempts.LogAttemptAsync(s.LiveId, AttemptType.Send));
        }

        Assert.Equal(0, await CountAsync(h));
    }

    public static async Task ListAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        await SeedAttemptAsync(h, s.LiveId, s.Member.Id);
        var attempts = s.Attempts(viewer);

        if (DraftExposureScenario.SeesDrafts(viewer))
        {
            Assert.Single(await attempts.GetAttemptsForBoulderAsync(s.LiveId));
        }
        else if (viewer == DraftViewer.ShareNonMember)
        {
            // Who ticked what used to be readable by any signed-in user for any boulder.
            Assert.Empty(await attempts.GetAttemptsForBoulderAsync(s.LiveId));
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => attempts.GetAttemptsForBoulderAsync(s.LiveId));
        }
    }

    public static async Task DeleteAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var memberTick = await SeedAttemptAsync(h, s.LiveId, s.Member.Id);
        var ownerTick = await SeedAttemptAsync(h, s.LiveId, h.Owner.Id);
        var attempts = s.Attempts(viewer);

        await Assert.ThrowsAnyAsync<Exception>(() => attempts.DeleteAttemptAsync(ownerTick));
        if (DraftExposureScenario.SeesDrafts(viewer))
        {
            await attempts.DeleteAttemptAsync(memberTick);
            Assert.Equal(1, await CountAsync(h));
            return;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => attempts.DeleteAttemptAsync(memberTick));
        Assert.Equal(2, await CountAsync(h));
    }

    /// <summary>The member is on a second wall too: their own session ticks there, the tablet acting as them does not.</summary>
    public static async Task KioskActAsStaysOnItsWallAsync(WallTestHarness h)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var otherBoulder = await CommentAccessChecks.SeedOtherWallAsync(h, s.Member.Id);
        var ownTick = await SeedAttemptAsync(h, otherBoulder, s.Member.Id);

        var kiosk = s.Attempts(DraftViewer.KioskActAsMember);
        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => kiosk.LogAttemptAsync(otherBoulder, AttemptType.Send));
        Assert.Equal("Boulder not found", refused.Message);
        await Assert.ThrowsAsync<InvalidOperationException>(() => kiosk.DeleteAttemptAsync(ownTick));
        Assert.Empty(await kiosk.GetAttemptsForBoulderAsync(otherBoulder));

        var own = s.Attempts(DraftViewer.Member);
        await own.LogAttemptAsync(otherBoulder, AttemptType.Flash);
        await own.DeleteAttemptAsync(ownTick);
        Assert.Equal(AttemptType.Flash, Assert.Single(await own.GetAttemptsForBoulderAsync(otherBoulder)).Type);
    }

    /// <summary>What the share route's tick, comment and favorite buttons follow: membership, as the services gate it.</summary>
    public static async Task IsMemberAsync(WallTestHarness h, DraftViewer viewer)
    {
        var s = await DraftExposureScenario.SeedAsync(h);
        var otherWall = await OtherWallIdAsync(h, await CommentAccessChecks.SeedOtherWallAsync(h, s.Member.Id));
        var walls = s.Walls(viewer);

        Assert.Equal(DraftExposureScenario.SeesDrafts(viewer), await walls.IsMemberAsync(s.WallId));

        // A member of the other wall too; only their own session may act there, never the tablet acting as them.
        Assert.Equal(viewer == DraftViewer.Member, await walls.IsMemberAsync(otherWall));
    }

    private static async Task<Guid> SeedAttemptAsync(WallTestHarness h, Guid boulderId, Guid userId)
    {
        await using var db = h.CreateContext();
        var attempt = new Attempt { BoulderId = boulderId, UserId = userId, Type = AttemptType.Attempt };
        db.Attempts.Add(attempt);
        await db.SaveChangesAsync();
        return attempt.Id;
    }

    private static async Task<Guid> OtherWallIdAsync(WallTestHarness h, Guid boulderId)
    {
        await using var db = h.CreateContext();
        return await db.Boulders.Where(b => b.Id == boulderId).Select(b => b.WallId).SingleAsync();
    }

    private static async Task<int> CountAsync(WallTestHarness h)
    {
        await using var db = h.CreateContext();
        return await db.Attempts.CountAsync();
    }
}
