// <copyright file="HoldStackOrderTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.HoldStacks;

namespace Blocwerk.Core.Tests.HoldStacks;

/// <summary>What is in front of what, from "hold to the hold it sits on".</summary>
public class HoldStackOrderTests
{
    private static readonly Guid Volume = Guid.NewGuid();
    private static readonly Guid Plate = Guid.NewGuid();
    private static readonly Guid Screw = Guid.NewGuid();
    private static readonly Guid Other = Guid.NewGuid();

    private static Dictionary<Guid, Guid?> Chain => new() { [Screw] = Plate, [Other] = null, [Plate] = Volume, [Volume] = null };

    [Fact]
    public void BelowAndAbove_FollowTheChainAtEveryDepth()
    {
        Assert.Equal([Plate, Volume], HoldStackOrder.Below(Chain, Screw));
        Assert.Empty(HoldStackOrder.Below(Chain, Volume));
        Assert.Equal([Screw, Plate], HoldStackOrder.Above(Chain, Volume));
        Assert.Empty(HoldStackOrder.Above(Chain, Screw));
    }

    [Fact]
    public void BackToFront_DrawsEveryBaseBeforeWhatSitsOnIt_AndLeavesTheRestInOrder()
    {
        var holds = new[] { (Screw, (Guid?)Plate), (Other, null), (Plate, Volume), (Volume, null) };

        var order = HoldStackOrder.BackToFront(holds, h => h.Item1, h => h.Item2).Select(h => h.Item1);

        Assert.Equal([Other, Volume, Plate, Screw], order);
    }

    [Fact]
    public void ALoop_IsRefused_AndOneInTheDataNeverHangs()
    {
        Assert.True(HoldStackOrder.WouldLoop(Chain, Volume, Volume));
        Assert.True(HoldStackOrder.WouldLoop(Chain, Volume, Screw));
        Assert.False(HoldStackOrder.WouldLoop(Chain, Other, Screw));

        var loop = new Dictionary<Guid, Guid?> { [Volume] = Plate, [Plate] = Volume, [Screw] = Guid.NewGuid() };
        Assert.Equal([Plate], HoldStackOrder.Below(loop, Volume));
        Assert.Empty(HoldStackOrder.Below(loop, Screw));
        Assert.Equal(3, HoldStackOrder.BackToFront(loop.Keys, id => id, id => loop[id]).Count);
    }
}
