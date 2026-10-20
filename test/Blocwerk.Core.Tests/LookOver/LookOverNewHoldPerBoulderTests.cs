// <copyright file="LookOverNewHoldPerBoulderTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enums;
using Blocwerk.Core.LookOver;
using Blocwerk.Core.Services;
using static Blocwerk.Core.Tests.LookOver.LookOverFixture;

namespace Blocwerk.Core.Tests.LookOver;

/// <summary>
/// "Should this boulder use the new hold in that place?", per boulder of a replaced hold with two boulders on it: all
/// yes is the merge, all no is "the old hold is gone", a mix moves the yes boulder and retires the no boulder with the
/// old hold, in one batch the duplicate review undoes exactly.
/// </summary>
public class LookOverNewHoldPerBoulderTests
{
    [Fact]
    public async Task AllYes_IsTheMerge_AndClearsTheReplacementsMarks()
    {
        using var h = new WallTestHarness();
        await NewHoldPerBoulderScenarios.AllYesAsync(h);
    }

    [Fact]
    public async Task AMix_MovesTheYesBoulder_AndTheNoBoulderBecomesHistoricWithTheOldHold()
    {
        using var h = new WallTestHarness();
        await NewHoldPerBoulderScenarios.MixedAsync(h);
    }

    [Fact]
    public async Task AllNo_IsTheOldHoldIsGone()
    {
        using var h = new WallTestHarness();
        await NewHoldPerBoulderScenarios.AllNoAsync(h);
    }

    [Fact]
    public async Task UndoingAMix_PutsEveryThingBackExactly()
    {
        using var h = new WallTestHarness();
        await NewHoldPerBoulderScenarios.UndoAsync(h, mixed: true);
    }

    [Fact]
    public async Task UndoingAllYes_PutsTheMarksBackToo()
    {
        using var h = new WallTestHarness();
        await NewHoldPerBoulderScenarios.UndoAsync(h, mixed: false);
    }

    [Fact]
    public async Task OnlyBouldersOnTheHold_AndOnlyWithUseTheNewHold_AreTaken()
    {
        using var h = new WallTestHarness();
        var (w, second, view) = await NewHoldPerBoulderScenarios.TwoBouldersAsync(h);
        var service = Service(h);

        var stranger = await Assert.ThrowsAsync<UserFacingException>(
            () => service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.UseNewHold, [w.BoulderId, second], [Guid.NewGuid()]));
        await Assert.ThrowsAsync<UserFacingException>(
            () => service.AnswerAsync(w.WallId, view.Id, LookOverAnswer.RemoveHold, [w.BoulderId, second], [w.BoulderId]));

        Assert.Equal(WallLookOverService.NotShownBoulder, stranger.Message);
        Assert.Equal(LookOverFindingStatus.Open, (await FindingAsync(h, w.WallId)).Status);
        Assert.NotNull(await HoldAsync(h, view.HoldId));
    }
}
