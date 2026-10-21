// <copyright file="WallEnhancementRealChainTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Enhancement.Steps;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Core.Tests.HoldColors;

namespace Blocwerk.Core.Tests.Enhancement;

/// <summary>The app's real steps and services on a wall with a colour photo: what runs, what is skipped, what lands where.</summary>
public sealed class WallEnhancementRealChainTests
{
    [Fact]
    public async Task TheRealChain_SuggestsColoursInTheirOwnWallBatch_SkipsWhatThisWallCannotDo_AndKeepAllConfirms()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        f.UseRealActors = true;
        await f.SetPanelPhotoAsync(HoldColorScenario.Photo());
        var blue = await f.AddHoldAsync(HoldColorScenario.BlueSpot.X, HoldColorScenario.BlueSpot.Y, h => h.Radius = 0.07);
        var red = await f.AddHoldAsync(HoldColorScenario.RedSpot.X, HoldColorScenario.RedSpot.Y, h => h.Radius = 0.07);
        var service = f.Service(
            new OutlinesEnhancementStep(), new StacksEnhancementStep(), new CleanupEnhancementStep(), new ColoursEnhancementStep(),
            new DuplicatesEnhancementStep(), new LinksEnhancementStep(), new NewHoldsEnhancementStep());

        var view = await f.StartAndWaitAsync(service);

        Assert.Equal(WallEnhancementState.Review, view.State);
        var outcomes = view.Steps.ToDictionary(s => s.Key, s => s.Outcome);
        Assert.Equal(WallEnhancementStepOutcomeKind.Skipped, outcomes["outlines"]);
        Assert.Equal(WallEnhancementStepOutcomeKind.Skipped, outcomes["links"]);
        Assert.Equal(WallEnhancementStepOutcomeKind.Skipped, outcomes["new-holds"]);
        Assert.Equal(WallEnhancementStepOutcomeKind.Done, outcomes["colours"]);
        Assert.True(view.Counts.Any(), string.Join(" | ", view.Steps.Select(st => $"{st.Key}: {st.Summary}")));
        var colours = (await f.ItemsAsync()).Where(i => i.Kind == WallEnhancementItemKind.Colour).ToDictionary(i => i.HoldId!.Value, i => i.Detail);
        Assert.Equal("blue", colours[blue]);
        Assert.Equal("red", colours[red]);
        var batch = Assert.Single(await f.BatchesAsync(), b => b.Label == HoldColorProposalService.BatchLabel);
        Assert.Equal((ChangeJournalScopeKind.Wall, f.WallId), (batch.ScopeKind, batch.ScopeId));
        Assert.True((await f.GetHoldAsync(blue)).HasProposedColor);

        var (accepted, skipped) = await service.AcceptAllAsync(f.WallId, WallEnhancementItemKind.Colour);

        Assert.Equal((2, 0), (accepted, skipped));
        Assert.Equal(("blue", null), ((await f.GetHoldAsync(blue)).Color, (await f.GetHoldAsync(blue)).ProposedColor));
        var crop = await service.CropAsync(f.WallId, (await f.ItemsAsync()).First(i => i.Kind == WallEnhancementItemKind.Colour).Id);
        Assert.NotNull(crop);
    }
}
