// <copyright file="WallUpdateFollowUpTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>
/// The review follow-ups: several card answers that stop part-way say where; staged-hold edits count as activity on
/// the update; an idle update whose starter lost the admin role is still closed (by the wall's owner).
/// </summary>
public class WallUpdateFollowUpTests
{
    [Fact]
    public async Task SeveralAnswers_StoppingPartWay_SayHowManyWereSavedAndWhichCardFailed()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        var old = await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        var card = await AddCardAsync(h, view, old[0].Id);
        var unknown = Guid.NewGuid();

        var stopped = await Assert.ThrowsAsync<RefreshCheckAnswersStoppedException>(() => s.Service.DecideChecksAsync(
            view.Id, [new RefreshCheckAnswer(card, UpdateExceptionAnswer.Keep), new RefreshCheckAnswer(unknown, UpdateExceptionAnswer.Keep)]));

        Assert.Equal((1, unknown), (stopped.Answered, stopped.FailedCheckId));
        Assert.Equal(UpdateExceptionStatus.Kept, (await s.Service.GetChecksAsync(view.Id)).Single(c => c.Id == card).Status);
    }

    [Fact]
    public async Task TooManyAnswersAtOnce_AreRefused()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        s.Capture.Client.IsConfigured = false;
        await s.SeedWallAsync();
        var view = await s.PrepareAsync();
        var answers = Enumerable.Range(0, WallRefreshService.MaxCheckAnswers + 1)
            .Select(_ => new RefreshCheckAnswer(Guid.NewGuid(), UpdateExceptionAnswer.Keep)).ToList();

        await Assert.ThrowsAsync<UserFacingException>(() => s.Service.DecideChecksAsync(view.Id, answers));
    }

    [Fact]
    public async Task AStagedHoldEdit_CountsAsActivityOnTheUpdate()
    {
        using var h = new WallTestHarness();
        await h.SeedWallAsync(holdCount: 1);
        WallUpdateSessionFixture.NoDetections(h);
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, WallUpdateSessionFixture.CentrePhoto());
        var session = (await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId))!;
        await AgeSessionsAsync(h, TimeSpan.FromDays(8));
        var centre = await WallUpdateSessionFixture.PanelIdAsync(h, 0, 0);
        var panels = WallUpdateSessionFixture.Panels(h);

        var added = await panels.AddStagedHoldInSessionAsync(h.WallId, session.Id, centre, 0.3, 0.3, 0.02);
        var afterAdd = (await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId))!.UpdatedAt;
        await AgeSessionsAsync(h, TimeSpan.FromDays(8));
        await panels.DeleteStagedHoldInSessionAsync(h.WallId, session.Id, added);
        var afterDelete = (await WallUpdateSessionFixture.Sessions(h).GetOpenSessionAsync(h.WallId))!.UpdatedAt;

        Assert.True(afterAdd > DateTimeOffset.UtcNow.AddMinutes(-1), "an add must count as activity");
        Assert.True(afterDelete > DateTimeOffset.UtcNow.AddMinutes(-1), "a delete must count as activity");
    }

    [Fact]
    public async Task AnIdleUpdate_WhoseStarterIsNoLongerAnAdmin_IsClosedByTheWallsOwner()
    {
        using var h = new WallTestHarness();
        using var s = new RefreshScenario(h);
        await s.SeedWallAsync();
        var helper = await h.AddMemberAsync("helper@test", WallRole.Admin);
        h.ActingUser = helper;
        await WallUpdateSessionFixture.BigUpdate(h).StageAsync(h.WallId, [new BigUpdatePhoto(CaptureScenario.TinyJpeg(70), "image/jpeg", 0, 0)]);
        h.ActingUser = h.Owner;
        await using (var db = h.CreateContext())
        {
            (await db.WallMembers.SingleAsync(m => m.UserId == helper.Id)).Role = WallRole.Member;
            await db.SaveChangesAsync();
        }

        await AgeSessionsAsync(h, TimeSpan.FromDays(8));

        Assert.Equal(1, await s.Processor.DiscardIdleSessionsAsync(DateTimeOffset.UtcNow, CancellationToken.None));
        await using var check = h.CreateContext();
        Assert.Equal(WallUpdateSessionStatus.Discarded, (await check.WallUpdateSessions.SingleAsync()).Status);
    }

    private static async Task AgeSessionsAsync(WallTestHarness h, TimeSpan by)
    {
        await using var db = h.CreateContext();
        foreach (var session in await db.WallUpdateSessions.ToListAsync())
        {
            session.UpdatedAt -= by;
        }

        await db.SaveChangesAsync();
    }

    private static async Task<Guid> AddCardAsync(WallTestHarness h, WallRefreshView view, Guid oldHoldId)
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
        };
        db.WallUpdateExceptions.Add(card);
        await db.SaveChangesAsync();
        return card.Id;
    }
}
