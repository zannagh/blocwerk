// <copyright file="WallRefreshOffCentreCheckTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// A "possibly removed" card about a hold on a panel other than the centre (review C4): a removal there is reset to
/// "carried" at the promote, so the card no longer takes "Remove" and never reads "Removed" for a hold that stays; and
/// several cards can be answered in one call.
/// </summary>
public class WallRefreshOffCentreCheckTests
{
    [Fact]
    public async Task OffTheCentre_APossiblyRemovedCard_TakesNoRemove()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        await MoveToSidePanelAsync(h, old[0].Id);
        var card = await AddCardAsync(h, view, old[0].Id, UpdateExceptionStatus.Pending);

        Assert.False((await s.Service.GetChecksAsync(view.Id)).Single(c => c.Id == card).CanRemove);
        var refused = await Assert.ThrowsAnyAsync<UserFacingException>(() => s.Service.DecideCheckAsync(view.Id, card, UpdateExceptionAnswer.Remove));

        Assert.Equal(WallUpdateSessionService.OffCentreRemoveRefused, refused.Message);
        var decisions = await WallUpdateSessionFixture.Sessions(h).GetDecisionsAsync(h.WallId);
        Assert.DoesNotContain(decisions.Carryover, d => d.OldHoldId == old[0].Id && d.Kind == CarryKind.Removed);
    }

    [Fact]
    public async Task OffTheCentre_ARemovalStoredEarlier_ReadsAsOpen_NotRemoved()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        await MoveToSidePanelAsync(h, old[0].Id);
        var card = await AddCardAsync(h, view, old[0].Id, UpdateExceptionStatus.Removed);

        var check = (await s.Service.GetChecksAsync(view.Id)).Single(c => c.Id == card);

        Assert.Equal((UpdateExceptionStatus.Pending, false), (check.Status, check.CanRemove));
    }

    [Fact]
    public async Task SeveralCards_AreAnsweredInOneCall_AndTheSummaryIsMadeOnce()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        var first = await AddCardAsync(h, view, old[0].Id, UpdateExceptionStatus.Pending);
        var second = await AddCardAsync(h, view, old[1].Id, UpdateExceptionStatus.Pending);

        var answered = await s.Service.DecideChecksAsync(
            view.Id, [new RefreshCheckAnswer(first, UpdateExceptionAnswer.Keep), new RefreshCheckAnswer(second, UpdateExceptionAnswer.Remove)]);
        await s.RunQueuedAsync();

        Assert.Equal(2, answered);
        var checks = await s.Service.GetChecksAsync(view.Id);
        Assert.Equal(UpdateExceptionStatus.Kept, checks.Single(c => c.Id == first).Status);
        Assert.Equal(UpdateExceptionStatus.Removed, checks.Single(c => c.Id == second).Status);
        var after = await s.CurrentAsync();
        Assert.NotEqual(view.Summary!.DecisionsVersion, after.Summary!.DecisionsVersion);
        Assert.False(after.SummaryUpdating);
    }

    /// <summary>Puts the old hold on a live panel to the right of the centre.</summary>
    private static async Task MoveToSidePanelAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        var side = new WallPanel { WallId = h.WallId, Col = 1, Row = 0, Generation = 0, Photo = [1, 2, 3], PhotoContentType = "image/jpeg" };
        db.WallPanels.Add(side);
        (await db.Holds.SingleAsync(x => x.Id == holdId)).WallPanelId = side.Id;
        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddCardAsync(WallTestHarness h, WallRefreshView view, Guid oldHoldId, UpdateExceptionStatus status)
    {
        await using var db = h.CreateContext();
        var panel = await db.WallPanels.AsNoTracking().FirstAsync(p => p.WallId == h.WallId && p.StagedPhoto != null);
        var card = new WallUpdateException
        {
            SessionId = view.UpdateSessionId!.Value,
            Kind = UpdateExceptionKind.PossiblyRemoved,
            OldHoldId = oldHoldId,
            PanelId = panel.Id,
            X = 0.5,
            Y = 0.5,
            PhotoScore = 0.05,
            TextureScore = 0.1,
            Status = status,
        };
        db.WallUpdateExceptions.Add(card);
        await db.SaveChangesAsync();
        return card.Id;
    }
}
