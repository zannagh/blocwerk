// <copyright file="ProbeReviewTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Enhancement;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests.Enhancement;

public sealed class ProbeReviewTests
{
    [Fact]
    public async Task Probe_ShapeUndo_AcrossOutlinesAndCleanup_WithColourBetween()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var service = f.Service(
            FakeEnhancementStep.Writing(f, "outlines", 100, WallEnhancementItemKind.Shape, [hold], h => h.Radius = 0.05),
            FakeEnhancementStep.Writing(f, "cleanup", 300, WallEnhancementItemKind.Shape, [hold], h => (h.Radius, h.ShapePoints) = (0.06, ShapePoint.DefaultOctagon(0.05))),
            FakeEnhancementStep.Writing(f, "colours", 400, WallEnhancementItemKind.Colour, [hold], h => (h.Color, h.ProposedColor) = ("red", "red")));
        await f.StartAndWaitAsync(service);
        var items = await f.ItemsAsync();
        Assert.Equal(2, items.Count);
        var shape = items.Single(i => i.Kind == WallEnhancementItemKind.Shape);
        var result = await service.DecideAsync(f.WallId, shape.Id, accept: false);
        var h = await f.GetHoldAsync(hold);
        Assert.True(result.Decision == WallEnhancementDecision.Rejected, result.Message);
        Assert.Equal(0.03, h.Radius, 6);
        Assert.Null(h.ShapePoints);
        Assert.Equal("red", h.Color);
    }

    [Fact]
    public async Task Probe_ColourUndo_AfterShapeKept()
    {
        using var f = await WallEnhancementFixture.CreateAsync();
        var hold = await f.AddHoldAsync(0.3, 0.3);
        var service = f.Service(
            FakeEnhancementStep.Writing(f, "outlines", 100, WallEnhancementItemKind.Shape, [hold], h => h.Radius = 0.05),
            FakeEnhancementStep.Writing(f, "colours", 400, WallEnhancementItemKind.Colour, [hold], h => (h.Color, h.ProposedColor) = ("red", "red")));
        await f.StartAndWaitAsync(service);
        var items = await f.ItemsAsync();
        var colour = items.Single(i => i.Kind == WallEnhancementItemKind.Colour);
        var result = await service.DecideAsync(f.WallId, colour.Id, accept: false);
        var h = await f.GetHoldAsync(hold);
        Assert.True(result.Decision == WallEnhancementDecision.Rejected, result.Message);
        Assert.Null(h.Color);
        Assert.Equal(0.05, h.Radius);
    }
}
