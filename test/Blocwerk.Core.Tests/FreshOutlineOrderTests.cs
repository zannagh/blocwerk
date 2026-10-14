// <copyright file="FreshOutlineOrderTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The same photo gives the same outlines every time: where two fresh outlines overlap, which one yields is decided
/// by where they lie on the photo, not by the random ids fresh holds get.
/// </summary>
public class FreshOutlineOrderTests
{
    [Fact]
    public void TwoOverlappingFreshOutlines_ResolveTheSameWay_WhateverIdsTheHoldsGot()
    {
        var results = Enumerable.Range(0, 12).Select(_ => Resolve()).ToList();

        Assert.All(results, r => Assert.Equal(results[0], r));

        // The one further up the photo goes first and keeps its square; the other one gives way.
        Assert.Equal(Key(Outline(Fresh(0.50, 0.40))), results[0].Upper);
        Assert.NotEqual(results[0].Upper, results[0].Lower);
    }

    [Fact]
    public void AFreshOutlineThatCannotBeClearedOfItsNeighbour_StaysAnOutline_AndDoesNotBecomeACircle()
    {
        // The second square lies almost on the first: no cut clears it and keeps half of it.
        var first = Fresh(0.50, 0.40);
        var second = Fresh(0.505, 0.405);
        var outlines = new Dictionary<Hold, HoldOutlineResult> { [first] = Outline(first), [second] = Outline(second) };

        var resolved = HoldShapeCleanup.ResolveOutlines([first, second], outlines, allowRadiusShrink: true, keepTraced: true);

        Assert.All(resolved.Values, r => Assert.NotEqual(HoldOutlineMethod.CircleFallback, r.Outline.Method));
        Assert.Equal(Key(Outline(second)), Key(resolved[second].Outline));
    }

    /// <summary>Two squares that overlap by a third, on holds with fresh random ids; returns each one's resolved outline.</summary>
    private static (string Upper, string Lower) Resolve()
    {
        var upper = Fresh(0.50, 0.40);
        var lower = Fresh(0.54, 0.44);
        var outlines = new Dictionary<Hold, HoldOutlineResult> { [upper] = Outline(upper), [lower] = Outline(lower) };

        var resolved = HoldShapeCleanup.ResolveOutlines([upper, lower], outlines, allowRadiusShrink: true);

        return (Key(resolved[upper].Outline), Key(resolved[lower].Outline));
    }

    private static string Key(HoldOutlineResult outline) =>
        outline.ShapePoints is { Count: >= 3 } shape ? string.Join(' ', shape.Select(p => FormattableString.Invariant($"{p.Dx:0.#####},{p.Dy:0.#####}"))) : "circle";

    private static Hold Fresh(double x, double y) => new() { X = x, Y = y, Radius = 0.03, IsAutoDetected = true };

    private static HoldOutlineResult Outline(Hold hold)
    {
        List<ShapePoint> shape = [new() { Dx = -0.03, Dy = -0.03 }, new() { Dx = 0.03, Dy = -0.03 }, new() { Dx = 0.03, Dy = 0.03 }, new() { Dx = -0.03, Dy = 0.03 }];
        var polygon = shape.Select(p => new NormalizedPoint(hold.X + p.Dx, hold.Y + p.Dy)).ToList();
        return new HoldOutlineResult(
            polygon, hold.X, hold.Y, shape, 100, new HoldOutlineBounds(hold.X - 0.03, hold.Y - 0.03, 0.06, 0.06), 0.9, HoldOutlineMethod.Contour, new HoldFingerprint());
    }
}
