// <copyright file="HoldOutlineSmoothingTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Web.Components.Shared;
using Xunit;

namespace Blocwerk.Core.Tests;

public class HoldOutlineSmoothingTests
{
    [Fact]
    public void Smooth_RoundsCornersWithoutTouchingTheStoredRing()
    {
        var ring = Square();

        var smooth = HoldOutlineSmoothing.Smooth(ring)!;

        Assert.Equal(16, smooth.Count);
        Assert.Equal(-1, ring[0].Dx);
        Assert.Equal(-1, ring[0].Dy);
        Assert.DoesNotContain(smooth, p => Math.Abs(p.Dx) == 1 && Math.Abs(p.Dy) == 1);
        Assert.All(smooth, p => Assert.True(Math.Abs(p.Dx) <= 1 && Math.Abs(p.Dy) <= 1));
    }

    [Fact]
    public void Smooth_KeepsTheShapeItsSize()
    {
        var smooth = HoldOutlineSmoothing.Smooth(Square())!;

        Assert.True(smooth.Max(p => p.Dx) > 0.9);
        Assert.True(smooth.Min(p => p.Dy) < -0.9);
    }

    [Fact]
    public void Smooth_LeavesNoPolygonAlone()
    {
        IReadOnlyList<ShapePoint> two = [new() { Dx = 0, Dy = 0 }, new() { Dx = 1, Dy = 1 }];

        Assert.Null(HoldOutlineSmoothing.Smooth(null));
        Assert.Same(two, HoldOutlineSmoothing.Smooth(two));
        Assert.Null(HoldOutlineSmoothing.SmoothRings(null));
    }

    [Theory]
    [InlineData(HoldType.Start, HoldUsage.FootOnly, BoulderHoldColors.Start)]
    [InlineData(HoldType.Top, HoldUsage.HandAndFoot, BoulderHoldColors.Top)]
    [InlineData(HoldType.Normal, HoldUsage.FootOnly, BoulderHoldColors.Foot)]
    [InlineData(HoldType.Normal, HoldUsage.HandOnly, BoulderHoldColors.Normal)]
    public void Outline_KeepsTheRoleColours(HoldType type, HoldUsage usage, string expected)
    {
        Assert.Equal(expected, BoulderHoldColors.Outline(type, usage));
    }

    private static List<ShapePoint> Square() =>
    [
        new() { Dx = -1, Dy = -1 },
        new() { Dx = 1, Dy = -1 },
        new() { Dx = 1, Dy = 1 },
        new() { Dx = -1, Dy = 1 },
    ];
}
