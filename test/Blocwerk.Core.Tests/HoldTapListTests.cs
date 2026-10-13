// <copyright file="HoldTapListTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Services;
using Blocwerk.Web.Components.Shared;
using Xunit;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The wall view's outlines on demand: the compact hold list the browser hit-tests, and the rule for which holds are
/// outlined, darker, next to the tapped one.
/// </summary>
public class HoldTapListTests
{
    [Fact]
    public void A_circle_hold_is_sent_as_centre_and_radius_in_ten_thousandths()
    {
        var item = Assert.Single(HoldTapList.Build([Circle(0.25, 0.5, 0.02, "yellow")]));

        Assert.Equal((2500, 5000, 200), (item.X, item.Y, item.Radius));
        Assert.Null(item.Outline);
        Assert.Null(item.Holes);
        Assert.False(item.Foot);
        Assert.Equal("#e6c800", item.Color);
        Assert.Equal("Yellow", item.ColorName);
        Assert.Null(item.Uses);
        Assert.Empty(item.Overlaps);
    }

    [Fact]
    public void A_shaped_hold_is_sent_as_its_absolute_outline_and_holes()
    {
        var hold = new PanelHold(
            Guid.NewGuid(), 0.5, 0.5, 0.05, null, HoldCategory.Foot,
            ShapePoints: Square(0.05),
            ShapeHoles: [Square(0.01), [new ShapePoint { Dx = 0, Dy = 0 }]]);

        var item = Assert.Single(HoldTapList.Build([hold]));

        Assert.Equal<int>([4500, 4500, 5500, 4500, 5500, 5500, 4500, 5500], item.Outline!.AsEnumerable());
        Assert.Equal<int>([4900, 4900, 5100, 4900, 5100, 5100, 4900, 5100], Assert.Single(item.Holes!).AsEnumerable());
        Assert.True(item.Foot);
        Assert.Null(item.Color);
        Assert.Null(item.ColorName);
    }

    [Fact]
    public void A_hold_the_last_update_did_not_find_is_left_out()
    {
        var shown = Circle(0.2, 0.2, 0.02);
        var kept = Circle(0.2, 0.2, 0.02) with { Kept = true };

        var item = Assert.Single(HoldTapList.Build([kept, shown]));

        Assert.Equal(2000, item.X);
        Assert.Empty(item.Overlaps);
    }

    [Fact]
    public void The_boulder_count_is_sent_when_usage_is_known_and_is_zero_for_an_unused_hold()
    {
        var used = Circle(0.2, 0.2, 0.02);
        var unused = Circle(0.6, 0.6, 0.02);

        var items = HoldTapList.Build([used, unused], new Dictionary<Guid, int> { [used.Id] = 3 });

        Assert.Equal([3, 0], items.Select(i => i.Uses));
    }

    [Fact]
    public void A_small_hold_sitting_on_a_large_one_overlaps_it_and_a_neighbour_does_not()
    {
        var volume = Circle(0.5, 0.5, 0.1);
        var onTop = Circle(0.53, 0.5, 0.02);
        var neighbour = Circle(0.75, 0.5, 0.02);

        var overlaps = HoldOutlineOverlap.Find([volume, onTop, neighbour]);

        Assert.Equal([1], overlaps[0]);
        Assert.Equal([0], overlaps[1]);
        Assert.Empty(overlaps[2]);
    }

    [Fact]
    public void Two_holds_that_only_graze_do_not_overlap_but_two_sharing_a_third_of_the_smaller_do()
    {
        var a = Circle(0.5, 0.5, 0.05);
        var grazing = Circle(0.59, 0.5, 0.05);
        var sharing = Circle(0.54, 0.5, 0.05);

        Assert.Empty(HoldOutlineOverlap.Find([a, grazing])[0]);
        Assert.Equal([1], HoldOutlineOverlap.Find([a, sharing])[0]);
    }

    [Fact]
    public void Overlap_is_measured_on_the_outline_not_on_the_circle_around_it()
    {
        // A thin horizontal bar: its bounding circle covers the small hold above it, its outline does not.
        var bar = new PanelHold(Guid.NewGuid(), 0.5, 0.5, 0.1, null, HoldCategory.Hand, ShapePoints: Box(0.1, 0.01));
        var above = Circle(0.5, 0.45, 0.02);
        var onBar = Circle(0.5, 0.5, 0.02);

        var items = HoldTapList.Build([bar, above, onBar]);

        Assert.Equal([2], items[0].Overlaps);
        Assert.Empty(items[1].Overlaps);
        Assert.Equal([0], items[2].Overlaps);
    }

    private static PanelHold Circle(double x, double y, double radius, string? color = null) =>
        new(Guid.NewGuid(), x, y, radius, color, HoldCategory.Hand);

    private static List<ShapePoint> Square(double half) => Box(half, half);

    private static List<ShapePoint> Box(double halfWidth, double halfHeight) =>
    [
        new() { Dx = -halfWidth, Dy = -halfHeight },
        new() { Dx = halfWidth, Dy = -halfHeight },
        new() { Dx = halfWidth, Dy = halfHeight },
        new() { Dx = -halfWidth, Dy = halfHeight },
    ];
}
