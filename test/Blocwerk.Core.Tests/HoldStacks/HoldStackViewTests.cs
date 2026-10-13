// <copyright file="HoldStackViewTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared;

namespace Blocwerk.Core.Tests.HoldStacks;

/// <summary>
/// Stacked holds on the surfaces that draw and move them: what the wall view's tap list tells the browser about a
/// stack, and the wall editor's move of a base with everything on it.
/// </summary>
public class HoldStackViewTests
{
    [Fact]
    public void TheTapList_SaysWhatEachHoldSitsOn_ByPosition_AndLeavesStackedHoldsOutOfTheOverlaps()
    {
        var volume = Circle(0.5, 0.5, 0.12);
        var plate = Circle(0.5, 0.5, 0.06) with { StackedOn = volume.Id };
        var screw = Circle(0.5, 0.5, 0.02) with { StackedOn = plate.Id };
        var crossing = Circle(0.6, 0.5, 0.03);

        var items = HoldTapList.Build([screw, crossing, volume, plate]);

        // Positions in the list: the screw sits on the plate (3), the plate on the volume (2).
        Assert.Equal([3, null, null, 2], items.Select(i => i.On));

        // The hold that merely lies across the volume's edge is still an overlap; the stack is not told twice.
        Assert.Empty(items[0].Overlaps);
        Assert.Equal([2], items[1].Overlaps);
        Assert.Equal([1], items[2].Overlaps);
        Assert.Empty(items[3].Overlaps);
    }

    [Fact]
    public void TheTapList_SkipsABaseThatIsNotShown_AndIgnoresOneThatIsGone()
    {
        var volume = Circle(0.5, 0.5, 0.12);
        var hidden = Circle(0.5, 0.5, 0.06) with { StackedOn = volume.Id, Kept = true };
        var onHidden = Circle(0.5, 0.5, 0.02) with { StackedOn = hidden.Id };
        var orphan = Circle(0.8, 0.8, 0.02) with { StackedOn = Guid.NewGuid() };

        var items = HoldTapList.Build([volume, hidden, onHidden, orphan]);

        // The hidden plate is not in the list: the hold on it reads as sitting on the volume under the plate.
        Assert.Equal(3, items.Count);
        Assert.Equal([null, 0, null], items.Select(i => i.On));
    }

    [Fact]
    public void AStackThatIsNotStored_IsStillToldAsAnOverlap()
    {
        var items = HoldTapList.Build([Circle(0.5, 0.5, 0.12), Circle(0.53, 0.5, 0.02)]);

        Assert.All(items, i => Assert.Null(i.On));
        Assert.Equal([1], items[0].Overlaps);
        Assert.Equal([0], items[1].Overlaps);
    }

    [Fact]
    public void MovingABase_TakesEverythingOnItAlong_AtAnyDepth_AndLeavesTheSizesAlone()
    {
        var volume = Hold(0.5, 0.5, 0.12);
        var plate = Hold(0.52, 0.48, 0.06, on: volume);
        var screw = Hold(0.53, 0.47, 0.02, on: plate);
        var beside = Hold(0.7, 0.5, 0.03);
        var holds = new List<Hold> { screw, beside, plate, volume };

        var moved = HoldGeometryEdits.MoveWithStack(holds, volume, 0.1, -0.2);

        Assert.Equal([volume.Id, screw.Id, plate.Id], moved.Select(h => h.Id));
        Assert.Equal((0.6, 0.3), (Math.Round(volume.X, 9), Math.Round(volume.Y, 9)));
        Assert.Equal((0.62, 0.28), (Math.Round(plate.X, 9), Math.Round(plate.Y, 9)));
        Assert.Equal((0.63, 0.27), (Math.Round(screw.X, 9), Math.Round(screw.Y, 9)));
        Assert.Equal((0.7, 0.5), (beside.X, beside.Y));
        Assert.Equal((0.12, 0.06, 0.02), (volume.Radius, plate.Radius, screw.Radius));

        // Moving a hold that sits on another leaves its base where it is.
        Assert.Equal([plate.Id, screw.Id], HoldGeometryEdits.MoveWithStack(holds, plate, 0.01, 0).Select(h => h.Id));
        Assert.Equal(0.6, Math.Round(volume.X, 9));
    }

    [Fact]
    public void ABaseStoppedByThePhotosEdge_MovesItsHoldsOnlyAsFarAsItWent_AndARefusedMoveMovesNothing()
    {
        var volume = Hold(0.9, 0.5, 0.12);
        var onIt = Hold(0.88, 0.5, 0.02, on: volume);
        var holds = new List<Hold> { volume, onIt };

        Assert.Equal(2, HoldGeometryEdits.MoveWithStack(holds, volume, 0.5, 0).Count);
        Assert.Equal((1, 0.98), (volume.X, Math.Round(onIt.X, 9)));

        Assert.Empty(HoldGeometryEdits.MoveWithStack(holds, volume, double.NaN, 0));
        Assert.Empty(HoldGeometryEdits.MoveWithStack(holds, volume, 0.5, 0));
        Assert.Equal(0.98, Math.Round(onIt.X, 9));
    }

    private static PanelHold Circle(double x, double y, double radius) => new(Guid.NewGuid(), x, y, radius, null, HoldCategory.Hand);

    private static Hold Hold(double x, double y, double radius, Hold? on = null) =>
        new() { X = x, Y = y, Radius = radius, StackedOnHoldId = on?.Id };
}
