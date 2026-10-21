// <copyright file="WallEnhancementServiceTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using NSubstitute;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>The one-time wall enhancement on SQLite: the chain, the "never twice per generation" rule, resume, review and gates.</summary>
public sealed class WallEnhancementServiceTests
{
    [Fact]
    public async Task FullCycle_ReviewFinalize_AndNeverTwicePerGeneration()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        await WallEnhancementScenarios.FullCycleAsync(f);
    }

    [Fact]
    public async Task TheUniqueIndex_RefusesASecondRunOfTheSameGeneration()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        await WallEnhancementScenarios.UniqueIndexAsync(f);
    }

    [Fact]
    public async Task ARunCutOffByARestart_ResumesWithTheInterruptedStep()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        await WallEnhancementScenarios.ResumeAsync(f);
    }

    [Fact]
    public async Task ARunWaitingForItsReview_CannotBeStartedAgain()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var step = FakeEnhancementStep.Writing(f, "outlines", 100, WallEnhancementItemKind.Shape, [hold], h => h.Radius = 0.05);
        var service = f.Service(step);
        await f.StartAndWaitAsync(service);

        await Assert.ThrowsAsync<UserFacingException>(() => service.StartAsync(f.WallId));

        Assert.Equal(1, step.Runs);
        Assert.Single(await RunsAsync(f));
    }

    [Fact]
    public async Task UndoingAHoldSomeoneEditedSince_LeavesItAsItIs()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var service = f.Service(FakeEnhancementStep.Writing(f, "outlines", 100, WallEnhancementItemKind.Shape, [hold], h => h.Radius = 0.05));
        await f.StartAndWaitAsync(service);
        await f.EditHoldAsync(hold, h => h.Radius = 0.08);

        var result = await service.DecideAsync(f.WallId, (await f.ItemsAsync()).Single().Id, accept: false);

        Assert.Equal(WallEnhancementDecision.Skipped, result.Decision);
        Assert.Equal(0.08, (await f.GetHoldAsync(hold)).Radius);
        Assert.Equal(WallEnhancementDecision.Skipped, (await f.ItemsAsync()).Single().Decision);
    }

    [Fact]
    public async Task AFailingStep_IsRecorded_AndTheNextStepStillRuns()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var broken = new FakeEnhancementStep("outlines", 100, WallEnhancementItemKind.Shape, (_, _) => throw new InvalidOperationException("boom"));
        var next = FakeEnhancementStep.Writing(f, "colours", 400, WallEnhancementItemKind.Colour, [hold], h => (h.Color, h.ProposedColor) = ("blue", "blue"));
        var service = f.Service(broken, next);

        var view = await f.StartAndWaitAsync(service);

        Assert.Equal(WallEnhancementState.Review, view.State);
        Assert.Equal(1, next.Runs);
        var failed = view.Steps.Single(s => s.Key == "outlines");
        Assert.Equal(WallEnhancementStepOutcomeKind.Failed, failed.Outcome);
        Assert.Contains("boom", failed.Summary);
    }

    [Fact]
    public async Task AWallUpdateDuringTheRun_StopsIt_AndTheNewGenerationCanBeEnhanced()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var bump = new FakeEnhancementStep("outlines", 100, WallEnhancementItemKind.Shape, async (_, _) =>
        {
            await f.BumpGenerationAsync();
            return WallEnhancementStepOutcome.Done("bumped");
        });
        var later = FakeEnhancementStep.Suggesting("duplicates", 500, WallEnhancementItemKind.Duplicate);
        var service = f.Service(bump, later);

        var view = await f.StartAndWaitAsync(service);

        Assert.Equal(0, later.Runs);
        Assert.Equal(WallEnhancementState.NotStarted, view.State);
        var stopped = Assert.Single(await RunsAsync(f));
        Assert.Equal(WallEnhancementStatus.Failed, stopped.Status);
        Assert.Contains("updated", stopped.Error);
    }

    [Fact]
    public async Task TheJob_WaitsWhileSomeoneHasTheHoldEditorOpen()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var asked = 0;
        f.Presence = Substitute.For<IWallEditPresence>();
        f.Presence.IsBeingEdited(f.WallId).Returns(_ => ++asked < 4);
        var step = FakeEnhancementStep.Suggesting("duplicates", 500, WallEnhancementItemKind.Duplicate);

        var view = await f.StartAndWaitAsync(f.Service(step));

        Assert.Equal(WallEnhancementState.Review, view.State);
        Assert.True(asked >= 4, $"asked {asked} times");
        Assert.Equal(1, step.Runs);
    }

    [Fact]
    public async Task AKioskTablet_AndANonAdmin_AreRefused()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        f.Kiosk = Substitute.For<IKioskContext>();
        f.Kiosk.IsKiosk.Returns(true);
        await Assert.ThrowsAsync<KioskRestrictedException>(() => f.Service().StartAsync(f.WallId));

        f.Kiosk = null;
        f.Harness.ActingUser = await f.Harness.AddMemberAsync("member@test", WallRole.Member);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => f.Service().GetAsync(f.WallId));
        Assert.Empty(await RunsAsync(f));
    }

    [Fact]
    public async Task AnOpenWallUpdate_BlocksTheStart()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        await using (var db = f.Harness.CreateContext())
        {
            db.WallUpdateSessions.Add(new WallUpdateSession { WallId = f.WallId, Status = WallUpdateSessionStatus.Open });
            await db.SaveChangesAsync();
        }

        var service = f.Service();

        Assert.NotNull((await service.GetAsync(f.WallId)).BlockedReason);
        await Assert.ThrowsAsync<UserFacingException>(() => service.StartAsync(f.WallId));
    }

    private static async Task<List<WallEnhancementRun>> RunsAsync(WallEnhancementFixture f)
    {
        await using var db = f.Harness.CreateContext();
        return [.. db.WallEnhancementRuns.Where(r => r.WallId == f.WallId)];
    }
}
