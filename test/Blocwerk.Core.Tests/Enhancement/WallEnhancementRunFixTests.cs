// <copyright file="WallEnhancementRunFixTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;
using NSubstitute;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>The review's fixes: undo past a 3D refinement, the generation after a wait, retry, finalize without, discard.</summary>
public sealed class WallEnhancementRunFixTests
{
    [Fact]
    public async Task UndoingAnOutline_AfterThe3DRefinementRewroteItsSizes_RestoresTheShape_KeepsTheNewSizes_AndRefinesAgain()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var service = f.Service(FakeEnhancementStep.Writing(f, "outlines", 100, WallEnhancementItemKind.Shape, [hold], h =>
        {
            (h.Radius, h.ShapePoints) = (0.05, ShapePoint.DefaultOctagon(0.05));
            (h.WidthMm, h.AreaMm2) = (30, 700);
        }));
        await f.StartAndWaitAsync(service);
        await f.EditHoldAsync(hold, h => (h.WidthMm, h.FacetId) = (42, "f1"));

        var result = await service.DecideAsync(f.WallId, (await f.ItemsAsync()).Single().Id, accept: false);

        Assert.Equal(WallEnhancementDecision.Rejected, result.Decision);
        var back = await f.GetHoldAsync(hold);
        Assert.Equal((0.03, null), (back.Radius, back.ShapePoints));
        Assert.Equal((42.0, "f1"), (back.WidthMm, back.FacetId));
        Assert.Null(back.AreaMm2);
        f.RefinementQueue.Received(1).Enqueue(f.WallId, Arg.Is<IEnumerable<Guid>>(ids => ids.Single() == hold));
    }

    [Fact]
    public async Task AWallUpdateDuringTheEditorWait_StopsTheRunBeforeTheStepWrites()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var asked = 0;
        f.Presence = Substitute.For<IWallEditPresence>();
        f.Presence.IsBeingEdited(f.WallId).Returns(_ =>
        {
            if (++asked == 2)
            {
                f.BumpGenerationAsync().GetAwaiter().GetResult();
            }

            return asked < 3;
        });
        var step = FakeEnhancementStep.Writing(f, "outlines", 100, WallEnhancementItemKind.Shape, [hold], h => h.Radius = 0.05);

        await f.StartAndWaitAsync(f.Service(step));

        Assert.Equal(0, step.Runs);
        Assert.Equal(0.03, (await f.GetHoldAsync(hold)).Radius);
        Assert.Equal(WallEnhancementStatus.Failed, (await f.RunAsync()).Status);
    }

    [Fact]
    public async Task AWallUpdate_IsRefusedWhileTheWallIsBeingEnhanced_AndAnOpenOneStopsTheRun()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        await using (var db = f.Harness.CreateContext())
        {
            db.WallEnhancementRuns.Add(new WallEnhancementRun { WallId = f.WallId, Generation = 0, CreatedByUserId = f.Harness.Owner.Id });
            await db.SaveChangesAsync();
            await Assert.ThrowsAsync<UserFacingException>(() => WallEnhancementGuard.EnsureNotRunningAsync(db, f.WallId));
            db.WallUpdateSessions.Add(new WallUpdateSession { WallId = f.WallId, Status = WallUpdateSessionStatus.Open });
            await db.SaveChangesAsync();
            Assert.NotNull(await WallEnhancementGuard.OpenUpdateReasonAsync(db, f.WallId));
        }

        var step = FakeEnhancementStep.Suggesting("duplicates", 500, WallEnhancementItemKind.Duplicate);
        var service = f.Service(step);
        f.Runner!.Launch((await f.RunAsync()).Id);
        await f.Runner.WhenIdleAsync((await f.RunAsync()).Id);

        Assert.Equal(0, step.Runs);
        Assert.Equal(WallEnhancementStatus.Failed, (await f.RunAsync()).Status);
        await Assert.ThrowsAsync<UserFacingException>(() => service.StartAsync(f.WallId));
    }

    [Fact]
    public async Task AFailedStep_CanBeRetried_AndFinalizeNeedsAnExplicitWithoutWhileOneFailed()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var attempts = 0;
        var flaky = new FakeEnhancementStep("outlines", 100, WallEnhancementItemKind.Shape, async (ctx, ct) =>
        {
            if (++attempts == 1)
            {
                throw new InvalidOperationException("boom");
            }

            return await FakeEnhancementStep.Writing(f, "x", 0, WallEnhancementItemKind.Shape, [hold], h => h.Radius = 0.05).RunAsync(ctx, ct);
        });
        var broken = new FakeEnhancementStep("colours", 400, WallEnhancementItemKind.Colour, (_, _) => throw new InvalidOperationException("bang"));
        var service = f.Service(flaky, broken);
        await f.StartAndWaitAsync(service);

        var retried = await service.RetryStepAsync(f.WallId, "outlines");
        await f.Runner!.WhenIdleAsync(retried.RunId!.Value);

        var view = await service.GetAsync(f.WallId);
        Assert.Equal(WallEnhancementStepOutcomeKind.Done, view.Steps.Single(s => s.Key == "outlines").Outcome);
        Assert.Equal(1, view.Counts.Single(c => c.Kind == WallEnhancementItemKind.Shape).Pending);
        await service.AcceptAllAsync(f.WallId, WallEnhancementItemKind.Shape);
        Assert.Contains("failed", (await Assert.ThrowsAsync<UserFacingException>(() => service.FinalizeAsync(f.WallId))).Message);

        var done = await service.FinalizeAsync(f.WallId, withoutFailedSteps: true);

        Assert.Equal(WallEnhancementState.Finalized, done.State);
        Assert.Contains("colours", (await f.RunAsync()).OutcomeJson);
    }

    [Fact]
    public async Task DiscardingARun_TakesBackItsWritesAndAnswers_AndTheGenerationCanRunAgain()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var (shaped, coloured) = (await f.AddHoldAsync(0.3, 0.3), await f.AddHoldAsync(0.7, 0.7));
        var service = f.Service(
            FakeEnhancementStep.Writing(f, "outlines", 100, WallEnhancementItemKind.Shape, [shaped], h => h.Radius = 0.05),
            FakeEnhancementStep.Writing(f, "colours", 400, WallEnhancementItemKind.Colour, [coloured], h => (h.Color, h.ProposedColor) = ("red", "red")));
        await f.StartAndWaitAsync(service);
        await service.AcceptAllAsync(f.WallId, WallEnhancementItemKind.Colour);
        Assert.Null((await f.GetHoldAsync(coloured)).ProposedColor);

        var result = await service.DiscardAsync(f.WallId);

        Assert.Equal((2, 1, 0), (result.HoldsRestored, result.AnswersTakenBack, result.LeftAsTheyAre));
        Assert.Equal(0.03, (await f.GetHoldAsync(shaped)).Radius);
        Assert.Null((await f.GetHoldAsync(coloured)).Color);
        await using (var db = f.Harness.CreateContext())
        {
            Assert.False(await db.WallEnhancementRuns.AnyAsync());
        }

        Assert.Equal(WallEnhancementState.NotStarted, (await service.GetAsync(f.WallId)).State);
        Assert.Equal(WallEnhancementState.Review, (await f.StartAndWaitAsync(service)).State);
    }
}
