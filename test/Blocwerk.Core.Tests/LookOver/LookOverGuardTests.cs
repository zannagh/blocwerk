// <copyright file="LookOverGuardTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Refresh;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.Automation;
using Blocwerk.Web.Controllers;
using Blocwerk.Web.Endpoints;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// What keeps an answer honest: it is for this wall's finding only, never from a kiosk tablet, only what the user may
/// do with a hold, and only with the boulders the user was shown when asked.
/// </summary>
public class LookOverGuardTests
{
    [Fact]
    public async Task AFindingOfOneWall_IsNotFoundThroughAnotherWall_OnThePageTheCropsAndTheApi()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var other = await AddOtherWallAsync(h);
        var findingId = (await FindingAsync(h, w.WallId)).Id;
        var service = Service(h);

        await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(other, findingId, LookOverAnswer.Settle));
        await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(other, findingId, LookOverAnswer.RemoveHold, []));
        Assert.Null(await service.GetCropAsync(other, findingId, CheckCropView.New));
        Assert.IsType<NotFound>(await WallLookOverCropEndpoint.HandleAsync(other, findingId, "new", new DefaultHttpContext(), service, CancellationToken.None));
        Assert.Empty(await service.ListAsync(other));
        var api = new WallLookOverApiController(service, NullLogger<WallLookOverApiController>.Instance).As(ApiKeys.Personal());
        var listed = Assert.IsType<LookOverFindingsResponse>(Assert.IsAssignableFrom<ObjectResult>(await api.Findings(other)).Value);
        Assert.Equal((false, 0, 0), (listed.State.Pending, listed.State.OpenFindings, listed.Findings.Count));
        Assert.Equal(LookOverFindingStatus.Open, (await FindingAsync(h, w.WallId)).Status);
    }

    [Theory]
    [InlineData("7")]
    [InlineData("0")]
    [InlineData("sideways")]
    public async Task ThePictureRoute_TakesTheThreeNamesOnly(string view)
    {
        var service = Substitute.For<IWallLookOverService>();

        var result = await WallLookOverCropEndpoint.HandleAsync(Guid.NewGuid(), Guid.NewGuid(), view, new DefaultHttpContext(), service, CancellationToken.None);

        Assert.IsType<NotFound>(result);
        Assert.Empty(service.ReceivedCalls());
    }

    [Theory]
    [InlineData("old", "private, max-age=3600")]
    [InlineData("Model", "private, no-store")]
    public async Task ThePictures_AreNeverPubliclyCached_AndTheModelPictureNotAtAll(string view, string cacheControl)
    {
        var service = Substitute.For<IWallLookOverService>();
        service.GetCropAsync(default, default, default, default).ReturnsForAnyArgs(new byte[] { 1 });
        var http = new DefaultHttpContext();

        var result = await WallLookOverCropEndpoint.HandleAsync(Guid.NewGuid(), Guid.NewGuid(), view, http, service, CancellationToken.None);

        Assert.IsType<FileContentHttpResult>(result);
        Assert.Equal(cacheControl, http.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task OnAKioskTablet_TheLookOverIsRefused_AndTheStateOffersNoWayIn()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var findingId = (await FindingAsync(h, w.WallId)).Id;
        var kiosk = Substitute.For<IKioskContext>();
        kiosk.IsKiosk.Returns(true);
        kiosk.KioskWallId.Returns(w.WallId);
        var service = Service(h, kiosk);

        Assert.False((await service.GetStateAsync(w.WallId))!.CanLookOver);
        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.ListAsync(w.WallId));
        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.AnswerAsync(w.WallId, findingId, LookOverAnswer.Settle));
        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.SettleAllAsync(w.WallId, LookOverFindingKind.UnsureMatch, onlyWithoutBoulders: false));
        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.FinishAsync(w.WallId, leaveOpen: true));
        await Assert.ThrowsAsync<KioskRestrictedException>(() => service.GetCropAsync(w.WallId, findingId, CheckCropView.New));
    }

    [Fact]
    public async Task AnInstallationAdminWhoIsNotOnTheWall_ReadsAcknowledgesAndFinishes_ButIsOfferedNothingThatChangesAHold()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        h.ActingUser = await AddInstallationAdminAsync(h);
        var service = Service(h);

        var state = (await service.GetStateAsync(w.WallId))!;
        var view = Assert.Single(await service.ListAsync(w.WallId));

        Assert.Equal((true, true), (state.Pending, state.CanLookOver));
        Assert.Equal([LookOverAnswer.Settle], view.Answers);
        Assert.Equal((false, "Crimp line"), (view.MergeNeedsAdmin, view.Boulders.Single().Name));
        Guid[] shown = [.. view.Boulders.Select(b => b.Id)];
        await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold, shown));
        await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, shown));
        Assert.NotNull(await HoldAsync(h, view.HoldId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.Settle);
        await service.FinishAsync(w.WallId, leaveOpen: false);
        Assert.Equal((Guid?)h.ActingUser.Id, (await WallAsync(h, w.WallId)).LookOverCompletedByUserId);
    }

    [Fact]
    public async Task AnAnswerThatChangesTheWall_IsRefused_WhenOtherBouldersUseTheHoldThanTheOnesShown()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.1);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        Guid[] shown = [.. view.Boulders.Select(b => b.Id)];
        await using (var db = h.CreateContext())
        {
            var late = new Boulder { WallId = w.WallId, Name = "Set meanwhile", CreatedByUserId = h.Owner.Id, Generation = 3 };
            db.Boulders.Add(late);
            db.BoulderHolds.Add(new BoulderHold { BoulderId = late.Id, HoldId = view.HoldId!.Value });
            await db.SaveChangesAsync();
        }

        foreach (var answer in new[] { LookOverAnswer.RemoveHold, LookOverAnswer.UseNewHold })
        {
            var refused = await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(w.WallId, view.Id, answer, shown));
            Assert.Equal(WallLookOverService.BouldersChanged, refused.Message);
        }

        // Saying which boulders were seen is not optional for these answers.
        await Assert.ThrowsAsync<UserFacingException>(() => service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold));
        Assert.NotNull(await HoldAsync(h, view.HoldId));
        Assert.False((await BoulderAsync(h, w.BoulderId)).IsHistoric);

        // After a reload the user sees both boulders and can go on.
        var reloaded = Assert.Single(await service.ListAsync(w.WallId));
        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold, [.. reloaded.Boulders.Select(b => b.Id)]);
        Assert.Null(await HoldAsync(h, view.HoldId));
    }

    [Fact]
    public async Task DifferentHold_TellsTheBoulderWhyItIsMarked_AsTheUpdateDoesForAReplacedHold()
    {
        using var h = new WallTestHarness();
        var w = await ApplyAsync(h, targetDetected: true, score: 0.4);
        var service = Service(h);
        var view = Assert.Single(await service.ListAsync(w.WallId));
        Assert.Empty(await h.BoulderService.GetBoulderMovesAsync(w.BoulderId));

        await service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.DifferentHold, [w.BoulderId]);

        await using var db = h.CreateContext();
        var record = await db.BoulderHoldMoves.SingleAsync();
        Assert.Equal(
            (HoldMoveOutcome.Replaced, w.BoulderId, (Guid?)w.TargetId, w.DetectionId, HoldType.Start, 3),
            (record.Outcome, record.BoulderId, record.OldHoldId, record.NewHoldId, record.Type, record.ToGeneration));
        var shown = Assert.Single(await h.BoulderService.GetBoulderMovesAsync(w.BoulderId));
        Assert.Equal(HoldMoveOutcome.Replaced, shown.Outcome);
        Assert.Contains("another hold is here now", shown.Text);
    }
}
