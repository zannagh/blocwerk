// <copyright file="LookOverFinishTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// The wall's flag stays until somebody who may look over the wall finishes: members only read the state, moderators
/// do everything but the merge (wall admins only, as in the duplicate review), and finishing with open findings has to
/// be said out loud.
/// </summary>
public class LookOverFinishTests
{
    [Fact]
    public async Task TheState_SaysSinceWhenAndHowMuchIsOpen()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);

        var state = (await Service(h).GetStateAsync(w.WallId))!;

        Assert.Equal((true, (int?)3, 1, 1, true), (state.Pending, state.Generation, state.OpenFindings, state.TotalFindings, state.CanLookOver));
        Assert.NotNull(state.PendingSince);
        Assert.Null(await Service(h).GetStateAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Finish_IsRefusedWhileFindingsAreOpen_UnlessSaidSo_AndLeavesTheBoulderMarked()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var service = Service(h);

        var refused = await Assert.ThrowsAsync<UserFacingException>(() => service.FinishAsync(w.WallId, leaveOpen: false));
        Assert.Contains("1 findings are still open", refused.Message);
        Assert.NotNull((await WallAsync(h, w.WallId)).LookOverPendingSince);

        await service.FinishAsync(w.WallId, leaveOpen: true);

        var wall = await WallAsync(h, w.WallId);
        Assert.Equal(((DateTimeOffset?)null, (Guid?)h.Owner.Id), (wall.LookOverPendingSince, wall.LookOverCompletedByUserId));
        Assert.NotNull(wall.LookOverCompletedAt);
        var state = (await service.GetStateAsync(w.WallId))!;
        Assert.Equal((false, 1, "Owner"), (state.Pending, state.OpenFindings, state.CompletedBy));
        Assert.True((await BoulderAsync(h, w.BoulderId)).NeedsReview);

        // What stayed open is still listed and can still be answered.
        Assert.Equal(LookOverFindingStatus.Open, Assert.Single(await service.ListAsync(w.WallId)).Status);
    }

    [Fact]
    public async Task Finish_WithEverythingAnswered_ClearsTheFlag_AndTheNextRoundStartsEmpty()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);
        Assert.Equal(1, (await service.SettleAllAsync(w.WallId, LookOverFindingKind.UnsureMatch, await OpenIdsAsync(h, w.WallId, LookOverFindingKind.UnsureMatch), onlyWithoutBoulders: false)).Settled);

        await service.FinishAsync(w.WallId, leaveOpen: false);

        var state = (await service.GetStateAsync(w.WallId))!;
        Assert.Equal((false, 0, 0), (state.Pending, state.OpenFindings, state.TotalFindings));
        Assert.Empty(await service.ListAsync(w.WallId));
    }

    [Fact]
    public async Task MarkAllUnsureMatchesAsTheSameHold_SettlesOnlyThose_AndClearsTheirMarks()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        Guid other;
        await using (var db = h.CreateContext())
        {
            var finding = new WallLookOverFinding { WallId = w.WallId, Generation = 3, Kind = LookOverFindingKind.NotFound };
            db.WallLookOverFindings.Add(finding);
            await db.SaveChangesAsync();
            other = finding.Id;
        }

        var settled = (await Service(h).SettleAllAsync(w.WallId, LookOverFindingKind.UnsureMatch, await OpenIdsAsync(h, w.WallId, LookOverFindingKind.UnsureMatch), onlyWithoutBoulders: false)).Settled;

        Assert.Equal(1, settled);
        Assert.False((await HoldAsync(h, w.DetectionId))!.NeedsReview);
        await using var after = h.CreateContext();
        var rows = await after.WallLookOverFindings.AsNoTracking().ToDictionaryAsync(f => f.Id, f => f.Status);
        Assert.Equal(LookOverFindingStatus.Open, rows[other]);
        Assert.Equal(LookOverFindingStatus.Settled, rows.Single(r => r.Key != other).Value);
    }

    [Fact]
    public async Task AMember_ReadsThatTheWallWasUpdated_ButCannotOpenAnswerOrFinish()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var findingId = (await FindingAsync(h, w.WallId)).Id;
        h.ActingUser = await AddMemberAsync(h, w.WallId, WallRole.Member);
        var service = Service(h);

        var state = (await service.GetStateAsync(w.WallId))!;

        Assert.Equal((true, false), (state.Pending, state.CanLookOver));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ListAsync(w.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.AnswerAsync(w.WallId, findingId, LookOverAnswer.Settle));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SettleAllAsync(w.WallId, LookOverFindingKind.UnsureMatch, [], onlyWithoutBoulders: false));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.FinishAsync(w.WallId, leaveOpen: true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetCropAsync(w.WallId, findingId, Blocwerk.Core.Refresh.CheckCropView.New));
        Assert.Equal(LookOverFindingStatus.Open, (await FindingAsync(h, w.WallId)).Status);
        Assert.NotNull((await WallAsync(h, w.WallId)).LookOverPendingSince);
    }

    [Fact]
    public async Task SomebodyWhoIsNotOnTheWall_SeesNoState()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        await using (var db = h.CreateContext())
        {
            var stranger = new User { Identifier = "stranger@test", DisplayName = "Stranger" };
            db.Users.Add(stranger);
            await db.SaveChangesAsync();
            h.ActingUser = stranger;
        }

        Assert.Null(await Service(h).GetStateAsync(w.WallId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => Service(h).ListAsync(w.WallId));
    }

    [Fact]
    public async Task AModerator_LooksOverAndFinishes_ButTheMergeStaysWithWallAdmins()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        h.ActingUser = await AddMemberAsync(h, w.WallId, WallRole.Moderator);
        var service = Service(h);

        var view = Assert.Single(await service.ListAsync(w.WallId));

        Assert.True((await service.GetStateAsync(w.WallId))!.CanLookOver);
        Assert.Equal([LookOverAnswer.Settle, LookOverAnswer.RemoveHold], view.Answers);
        Assert.True(view.MergeNeedsAdmin);
        await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, [.. view.Boulders.Select(b => b.Id)]));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold, [.. view.Boulders.Select(b => b.Id)]);
        await service.FinishAsync(w.WallId, leaveOpen: false);

        Assert.Null(await HoldAsync(h, view.HoldId));
        Assert.Equal((Guid?)h.ActingUser.Id, (await WallAsync(h, w.WallId)).LookOverCompletedByUserId);
    }
}
