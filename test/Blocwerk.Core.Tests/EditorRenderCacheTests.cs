// <copyright file="EditorRenderCacheTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Shared.Ui.Overlay;
using Blocwerk.Web.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace Blocwerk.Core.Tests;

/// <summary>
/// What keeps a wall editor render cheap on a big panel: the cached smooth-outline check, the render gating that keeps
/// an unchanged hold from re-rendering with the editor, and the memoised stack order.
/// </summary>
public class EditorRenderCacheTests
{
    [Fact]
    public void SmoothCheck_IsCached_UntilAPointMoves()
    {
        var hold = SmoothHold();
        Assert.NotNull(HoldOutlineSpline.CurrentControlPoints(hold));
        int before = HoldOutlineSplineCache.Computations;

        for (int i = 0; i < 5; i++)
        {
            Assert.NotNull(HoldOutlineSpline.CurrentControlPoints(hold));
        }

        Assert.Equal(before, HoldOutlineSplineCache.Computations);

        // Edited in place, as the editor does: the cache must notice and the outline no longer matches.
        hold.ShapeControlPoints![0].Dx += 0.002;
        Assert.Null(HoldOutlineSpline.CurrentControlPoints(hold));
        Assert.Equal(before + 1, HoldOutlineSplineCache.Computations);

        HoldOutlineSpline.Refresh(hold);
        Assert.NotNull(HoldOutlineSpline.CurrentControlPoints(hold));
    }

    [Fact]
    public void SmoothCheck_SeesAnOutlineRewrittenBehindTheControlPoints()
    {
        var hold = SmoothHold();
        Assert.True(HoldOutlineSpline.IsSmooth(hold));

        hold.ShapePoints = Square(0.01);

        Assert.False(HoldOutlineSpline.IsSmooth(hold));
    }

    [Fact]
    public void HoldShapeGeometry_IsComparedByValue()
    {
        // The stored points go through the Server's conversion, as every editor's do.
        var outline = Square(0.01);
        var holes = new List<IReadOnlyList<ShapePoint>> { Square(0.002) };
        var geometry = HoldShapeLook.Geometry(outline.AsDocumentRing(), holes.AsDocumentRings());

        Assert.True(HoldShapeLook.SameGeometry(geometry, outline.AsDocumentRing(), holes.AsDocumentRings()));
        Assert.Same(outline.AsDocumentRing(), outline.AsDocumentRing());
        Assert.True(HoldShapeLook.SameGeometry(geometry, Square(0.01).AsDocumentRing(), new List<IReadOnlyList<ShapePoint>> { Square(0.002) }.AsDocumentRings()));
        Assert.False(HoldShapeLook.SameGeometry(geometry, outline.AsDocumentRing(), null));
        Assert.False(HoldShapeLook.SameGeometry(geometry, null, holes.AsDocumentRings()));

        // Edited in place, as the editors do: the kept conversion must notice.
        outline[2].Dy += 1e-7;
        Assert.False(HoldShapeLook.SameGeometry(geometry, outline.AsDocumentRing(), holes.AsDocumentRings()));
        Assert.True(HoldShapeLook.SameGeometry(HoldShapeLook.Geometry(null, null), null, null));
    }

    [Fact]
    public void HoldShapeLook_SeesTheSameCallback_AsUnchanged_AndAFreshLambdaAsChanged()
    {
        var receiver = new object();
        Action handler = () => { };
        var stable = EventCallback.Factory.Create<MouseEventArgs>(receiver, handler);

        Assert.Equal(Look(stable), Look(stable));
        Assert.Equal(Look(stable), Look(EventCallback.Factory.Create<MouseEventArgs>(receiver, handler)));
        Assert.NotEqual(Look(stable), Look(EventCallback.Factory.Create<MouseEventArgs>(receiver, () => { })));
        Assert.NotEqual(Look(stable), Look(stable) with { Fill = "red" });
    }

    [Fact]
    public void StackMemo_IsReused_UntilAHoldOrABaseChanges()
    {
        var a = new Hold { Id = Guid.NewGuid() };
        var b = new Hold { Id = Guid.NewGuid(), StackedOnHoldId = null };
        var holds = new List<Hold> { b, a };
        var memo = HoldStackMemo.For(holds);

        Assert.True(memo.Matches(holds));
        Assert.Equal([b, a], memo.Order);

        b.StackedOnHoldId = a.Id;
        Assert.False(memo.Matches(holds));
        Assert.Equal([a, b], HoldStackMemo.For(holds).Order);

        var replaced = new List<Hold> { new() { Id = b.Id, StackedOnHoldId = a.Id }, a };
        Assert.False(HoldStackMemo.For(holds).Matches(replaced));
    }

    private static HoldShapeLook Look(EventCallback<MouseEventArgs> click) =>
        new(null, null, 0.5, 0.5, 0.01, false, 0.7, "white", "black", "0.25", null, false, null, null, click, default, default, default);

    private static Hold SmoothHold()
    {
        var hold = new Hold { Id = Guid.NewGuid(), X = 0.5, Y = 0.5, Radius = 0.01, ShapeControlPoints = Square(0.01) };
        HoldOutlineSpline.Refresh(hold);
        return hold;
    }

    private static List<ShapePoint> Square(double r) => Points((-r, -r), (r, -r), (r, r), (-r, r));

    private static List<ShapePoint> Points(params (double X, double Y)[] points) => [.. points.Select(p => new ShapePoint { Dx = p.X, Dy = p.Y })];
}
