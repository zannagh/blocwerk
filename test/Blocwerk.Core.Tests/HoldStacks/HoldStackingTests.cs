// <copyright file="HoldStackingTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.HoldStacks;

namespace Blocwerk.Core.Tests.HoldStacks;

/// <summary>The pure rule: which hold sits on which, from their outlines alone.</summary>
public class HoldStackingTests
{
    [Fact]
    public void AHoldInsideAClearlyLargerOne_SitsOnIt_AndNotTheOtherWayRound()
    {
        var (volume, small) = (Auto(0.5, 0.5, 0.1), Auto(0.53, 0.5, 0.02));

        Assert.True(HoldStacking.SitsOn(small, volume));
        Assert.False(HoldStacking.SitsOn(volume, small));
        Assert.True(HoldStacking.AreStack(volume, small));
    }

    [Theory]
    [InlineData(0.5, 0.04, false)] // the same size on the same spot: the same hold twice, never a stack
    [InlineData(0.5, 0.031, false)] // a little smaller (area ratio 1.66)
    [InlineData(0.5, 0.029, true)] // clearly smaller (area ratio 1.9)
    [InlineData(0.535, 0.02, false)] // clearly smaller, but half of it hangs over the edge
    [InlineData(0.7, 0.02, false)] // somewhere else
    public void EqualSizes_AndHoldsThatMostlyLieOutside_AreNotStacked(double x, double radius, bool stacked)
    {
        Assert.Equal(stacked, HoldStacking.SitsOn(Auto(x, 0.5, radius), Auto(0.5, 0.5, 0.04)));
    }

    [Fact]
    public void AConcaveOutline_CarriesWhatLiesOnIt_AndNotWhatLiesInItsNotch()
    {
        // An L: the left arm and the bottom arm, with the upper right quarter cut out.
        var volume = Shaped(0.5, 0.5, (-0.1, -0.1), (0, -0.1), (0, 0), (0.1, 0), (0.1, 0.1), (-0.1, 0.1));
        var onTheArm = Auto(0.45, 0.45, 0.02);
        var inTheNotch = Auto(0.55, 0.45, 0.02);

        Assert.True(HoldStacking.SitsOn(onTheArm, volume));
        Assert.False(HoldStacking.SitsOn(inTheNotch, volume));
    }

    [Fact]
    public void TheSmallestHoldItSitsOn_IsTheBase_SoAHoldOnAHoldOnAVolumeIsAChain()
    {
        var (volume, plate, screw) = (Auto(0.5, 0.5, 0.2), Auto(0.5, 0.5, 0.06), Auto(0.5, 0.5, 0.015));

        var plan = HoldStacking.Plan([screw, volume, plate]);

        Assert.Equal(
            new[] { (screw.Id, plate.Id), (plate.Id, volume.Id) },
            plan.Select(p => (p.Upper.Id, p.Base.Id)));
    }

    [Fact]
    public void HiddenAndVirtualHolds_AreNeverStackedAutomatically_NorIsACircleSomebodyPlacedABase()
    {
        var volume = Auto(0.5, 0.5, 0.1);
        var (kept, virt) = (Auto(0.5, 0.5, 0.02), Auto(0.52, 0.5, 0.02));
        (kept.KeptUnconfirmedGeneration, virt.IsVirtual) = (3, true);
        Assert.Empty(HoldStacking.Plan([volume, kept, virt]));

        // A circle placed by hand marks roughly where a hold is: the detection inside it is that hold.
        var placed = Auto(0.5, 0.5, 0.1);
        placed.IsAutoDetected = false;
        Assert.Empty(HoldStacking.Plan([placed, Auto(0.5, 0.5, 0.02)]));

        // A hand-drawn outline is a real outline, and a hold placed by hand can sit on a detection.
        var drawn = Shaped(0.5, 0.5, (-0.1, -0.1), (0.1, -0.1), (0.1, 0.1), (-0.1, 0.1));
        drawn.IsAutoDetected = false;
        var onTop = Auto(0.5, 0.5, 0.02);
        onTop.IsAutoDetected = false;
        Assert.Single(HoldStacking.Plan([drawn, onTop]));
    }

    [Fact]
    public void WithFreshHolds_OnlyPairsWithAFreshHoldAreStacked_SoWhatAPersonTookApartStaysApart()
    {
        var (volume, takenOff, fresh) = (Auto(0.5, 0.5, 0.1), Auto(0.47, 0.5, 0.02), Auto(0.54, 0.5, 0.02));

        var plan = HoldStacking.Plan([volume, takenOff, fresh], new HashSet<Guid> { fresh.Id });
        Assert.Equal([(fresh.Id, volume.Id)], plan.Select(p => (p.Upper.Id, p.Base.Id)));

        // A fresh volume takes the holds that lie on it and have no base.
        Assert.Equal(2, HoldStacking.Plan([volume, takenOff, fresh], new HashSet<Guid> { volume.Id }).Count);
    }

    [Fact]
    public void AHoldThatHasABase_KeepsIt_AndALoopIsNeverPlanned()
    {
        var (volume, small) = (Auto(0.5, 0.5, 0.1), Auto(0.5, 0.5, 0.02));

        // Somebody put the volume ON the small hold: stacking the small hold on the volume would close a loop.
        volume.StackedOnHoldId = small.Id;
        Assert.Empty(HoldStacking.Plan([volume, small]));

        // A base that is not on the photo (deleted) reads as no base.
        (volume.StackedOnHoldId, small.StackedOnHoldId) = (null, Guid.NewGuid());
        Assert.Single(HoldStacking.Plan([volume, small]));
    }

    [Fact]
    public void ACircleIsMeasuredInPixels_SoAWidePhotoDoesNotStretchIt()
    {
        // Radii are normalized to the long side: on a 2000 x 1000 photo this hold reaches 0.04 * 2000 px either way.
        var (volume, above) = (Auto(0.5, 0.5, 0.04), Auto(0.5, 0.565, 0.01));

        Assert.True(HoldStacking.SitsOn(above, volume, (2000, 1000)));
        Assert.False(HoldStacking.SitsOn(above, volume, (1000, 1000)));
    }

    internal static Hold Auto(double x, double y, double radius) =>
        new() { X = x, Y = y, Radius = radius, IsAutoDetected = true };

    internal static Hold Shaped(double x, double y, params (double Dx, double Dy)[] outline)
    {
        var hold = Auto(x, y, 0.02);
        hold.ShapePoints = [.. outline.Select(p => new ShapePoint { Dx = p.Dx, Dy = p.Dy })];
        return hold;
    }
}
