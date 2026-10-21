// <copyright file="WallEnhancementStopTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Data;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>Stopping a run: cancel before a step, live work after a cancel or a time-out, a kept answer, a flaky heartbeat.</summary>
public sealed class WallEnhancementStopTests
{
    [Fact]
    public async Task ACancelWhileWaitingForAnEditor_FailsEveryStepThatNeverRan_FinalizeRefuses_AndRetryRunsThem()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var editing = true;
        f.Presence = Substitute.For<IWallEditPresence>();
        f.Presence.IsBeingEdited(f.WallId).Returns(_ => editing);
        var first = FakeEnhancementStep.Suggesting("duplicates", 500, WallEnhancementItemKind.Duplicate);
        var second = FakeEnhancementStep.Suggesting("links", 600, WallEnhancementItemKind.Link);
        var service = f.Service(first, second);
        await service.StartAsync(f.WallId);
        await Task.Delay(200);

        var view = await service.CancelAsync(f.WallId);

        Assert.Equal(WallEnhancementState.Review, view.State);
        Assert.All(view.Steps, s => Assert.Equal(WallEnhancementStepOutcomeKind.Failed, s.Outcome));
        Assert.All(view.Steps, s => Assert.Contains("before it ran", s.Summary));
        Assert.Contains("failed", (await Assert.ThrowsAsync<UserFacingException>(() => service.FinalizeAsync(f.WallId))).Message);

        editing = false;
        var retried = await service.RetryStepAsync(f.WallId, "duplicates");
        await f.Runner!.WhenIdleAsync(retried.RunId!.Value);

        var after = await service.GetAsync(f.WallId);
        Assert.Equal(WallEnhancementState.Review, after.State);
        Assert.All(after.Steps, s => Assert.Equal(WallEnhancementStepOutcomeKind.Done, s.Outcome));
        Assert.Equal((1, 1), (first.Runs, second.Runs));
    }

    [Fact]
    public async Task ACancelWhileQueued_FailsEveryStepAsNeverRun()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var release = new TaskCompletionSource();
        var otherWall = await AddWallAsync(f);
        var blocking = new FakeEnhancementStep("links", 600, WallEnhancementItemKind.Link, async (ctx, ct) =>
        {
            if (ctx.WallId == otherWall)
            {
                await release.Task.WaitAsync(ct);
            }

            return WallEnhancementStepOutcome.Done("ok");
        });
        var service = f.Service(blocking);
        await service.StartAsync(otherWall);
        await service.StartAsync(f.WallId);

        var view = await service.CancelAsync(f.WallId);

        Assert.Equal(WallEnhancementState.Review, view.State);
        Assert.Contains("before it ran", Assert.Single(view.Steps).Summary);
        release.SetResult();
    }

    [Fact]
    public async Task WhileACancelledStepStillWorks_DiscardFinalizeRetryAndAWallUpdate_AllWait()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        f.StopGrace = TimeSpan.FromMilliseconds(100);
        var release = new TaskCompletionSource();
        var deaf = new FakeEnhancementStep("links", 600, WallEnhancementItemKind.Link, async (_, _) =>
        {
            await release.Task;
            return WallEnhancementStepOutcome.Done("late");
        });
        var service = f.Service(deaf);
        await service.StartAsync(f.WallId);
        await WaitUntilAsync(async () => (await f.RunAsync()).CurrentStep == "links");

        await service.CancelAsync(f.WallId);

        Assert.Equal(WallEnhancementStatus.Review, (await f.RunAsync()).Status);
        Assert.True(WallEnhancementRunner.IsLive((await f.RunAsync()).Id));
        await Assert.ThrowsAsync<UserFacingException>(() => service.DiscardAsync(f.WallId));
        await Assert.ThrowsAsync<UserFacingException>(() => service.FinalizeAsync(f.WallId, withoutFailedSteps: true));
        await Assert.ThrowsAsync<UserFacingException>(() => service.RetryStepAsync(f.WallId, "links"));
        await using (var db = f.Harness.CreateContext())
        {
            Assert.Equal(WallEnhancementGuard.StillStopping, (await Assert.ThrowsAsync<UserFacingException>(() => WallEnhancementGuard.EnsureNotRunningAsync(db, f.WallId))).Message);
        }

        release.SetResult();
        await f.Runner!.WhenIdleAsync((await f.RunAsync()).Id);
        Assert.False(WallEnhancementRunner.IsLive((await f.RunAsync()).Id));
        await service.DiscardAsync(f.WallId);
    }

    [Fact]
    public async Task AStepThatIgnoresItsTimeOut_StopsTheRunThere_AndTheNextStepNeverStarts()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        (f.StepTimeout, f.StopGrace) = (TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
        var release = new TaskCompletionSource();
        var deaf = new FakeEnhancementStep("links", 600, WallEnhancementItemKind.Link, async (_, _) =>
        {
            await release.Task;
            return WallEnhancementStepOutcome.Done("late");
        });
        var next = FakeEnhancementStep.Suggesting("new-holds", 700, WallEnhancementItemKind.NewHold);
        var service = f.Service(deaf, next);
        await service.StartAsync(f.WallId);
        await WaitUntilAsync(async () => (await f.RunAsync()).Status == WallEnhancementStatus.Failed);

        var run = await f.RunAsync();
        Assert.Contains("did not stop", run.Error);
        Assert.True(WallEnhancementRunner.IsLive(run.Id));
        await Assert.ThrowsAsync<UserFacingException>(() => service.StartAsync(f.WallId));

        release.SetResult();
        await f.Runner!.WhenIdleAsync(run.Id);
        Assert.Equal(0, next.Runs);
        Assert.Equal(WallEnhancementStatus.Failed, (await f.RunAsync()).Status);
    }

    [Fact]
    public async Task Discard_KeepsTheRecordOfAnAnswerWhoseWriteWasKept()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var proposalId = await AddProposalAsync(f);
        Guid added = default;
        f.Proposals.AcceptAsync(f.WallId, proposalId, Arg.Any<string?>(), Arg.Any<HoldCategory>(), Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            await using var db = await f.Journalling.CreateDbContextAsync();
            db.CurrentUserId = f.Harness.Owner.Id;
            var hold = new Hold { WallId = f.WallId, WallPanelId = f.PanelId, X = 0.9, Y = 0.9, Radius = 0.02 };
            db.Holds.Add(hold);
            var p = await db.HoldProposals.SingleAsync(x => x.Id == proposalId);
            (p.Status, p.HoldId, added) = (HoldProposalStatus.Accepted, hold.Id, hold.Id);
            await db.SaveChangesAsync();
            return hold;
        });
        var service = f.Service(FakeEnhancementStep.Suggesting("new-holds", 700, WallEnhancementItemKind.NewHold, new WallEnhancementSuggestion(null, null, proposalId, null)));
        await f.StartAndWaitAsync(service);
        await service.DecideAsync(f.WallId, (await f.ItemsAsync()).Single().Id, accept: true);
        await f.EditHoldAsync(added, h => h.Color = "red");

        var result = await service.DiscardAsync(f.WallId);

        Assert.Equal((0, 1, 0), (result.AnswersTakenBack, result.LeftAsTheyAre, result.RecordsRestored));
        await using var check = f.Harness.CreateContext();
        Assert.True(await check.Holds.AnyAsync(h => h.Id == added));
        var proposal = await check.HoldProposals.SingleAsync(p => p.Id == proposalId);
        Assert.Equal((HoldProposalStatus.Accepted, (Guid?)added), (proposal.Status, proposal.HoldId));
    }

    [Fact]
    public async Task TheHeartbeat_SurvivesAFailedSave()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        await using (var db = f.Harness.CreateContext())
        {
            db.WallEnhancementRuns.Add(new WallEnhancementRun { WallId = f.WallId, Generation = 0, CreatedByUserId = f.Harness.Owner.Id });
            await db.SaveChangesAsync();
        }

        var flaky = new FailOnceFactory(f.Journalling);
        var job = new WallEnhancementJob(flaky, new EnhancementTestActors(f), [], f.Journal, NullLogger<WallEnhancementJob>.Instance)
        {
            HeartbeatInterval = TimeSpan.FromMilliseconds(20),
        };
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));

        await job.HeartbeatAsync((await f.RunAsync()).Id, stop.Token);

        Assert.True(flaky.Failed);
        Assert.NotNull((await f.RunAsync()).HeartbeatAt);
    }

    private static async Task<Guid> AddWallAsync(WallEnhancementFixture f)
    {
        await using var db = f.Harness.CreateContext();
        var wall = new Wall { Name = "Other", OwnerId = f.Harness.Owner.Id, Photo = [1], PhotoContentType = "image/jpeg" };
        db.Walls.Add(wall);
        db.WallMembers.Add(new WallMember { WallId = wall.Id, UserId = f.Harness.Owner.Id, Role = WallRole.Admin });
        await db.SaveChangesAsync();
        return wall.Id;
    }

    private static async Task<Guid> AddProposalAsync(WallEnhancementFixture f)
    {
        await using var db = f.Harness.CreateContext();
        var model = new WallGeometryModel { WallId = f.WallId, Json = "{}", Source = "test" };
        db.WallGeometryModels.Add(model);
        var p = new HoldProposal { WallId = f.WallId, GeometryModelId = model.Id, FacetId = "f", BestPhoto = "p", Status = HoldProposalStatus.Pending };
        db.HoldProposals.Add(p);
        await db.SaveChangesAsync();
        return p.Id;
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var i = 0; i < 250 && !await condition(); i++)
        {
            await Task.Delay(20);
        }
    }
}
