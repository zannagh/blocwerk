// <copyright file="HoldOutlineSplineTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Services;
using Microsoft.EntityFrameworkCore;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The smooth hold outline: the curve itself, the control points taken from an existing outline, and what a
/// save stores, so that everything reading <see cref="Hold.ShapePoints"/> sees the smooth shape.
/// </summary>
public class HoldOutlineSplineTests
{
    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(16)]
    public void Curve_PassesThroughEveryControlPoint_InOrder(int count)
    {
        var control = Ring(count, 0.02, wobble: 0.3);

        var curve = HoldOutlineSpline.Densify(control);

        int step = curve.Count / count;
        Assert.Equal(count * step, curve.Count);
        for (int i = 0; i < count; i++)
        {
            Assert.Equal(control[i].Dx, curve[i * step].Dx, 5);
            Assert.Equal(control[i].Dy, curve[i * step].Dy, 5);
        }
    }

    [Theory]
    [InlineData(3, 0.0)]
    [InlineData(5, 0.0)]
    [InlineData(12, 0.0)]
    [InlineData(7, 0.9)]
    public void Curve_ThroughAConvexOutline_DoesNotCrossItself(int count, double stretch)
    {
        // Points on an ellipse at uneven angles: convex, with long and short edges next to each other.
        var control = Enumerable.Range(0, count)
            .Select(i => (2 * Math.PI * i / count) + (stretch * Math.Sin(i) / count))
            .Select(a => new ShapePoint { Dx = Math.Cos(a) * 0.03, Dy = Math.Sin(a) * 0.012 })
            .ToList();

        var curve = HoldOutlineSpline.Densify(control);

        Assert.False(CrossesItself(curve));
    }

    [Fact]
    public void Curve_HasABoundedNumberOfPoints()
    {
        for (int count = 3; count <= HoldOutlineSpline.MaxEditableControlPoints; count++)
        {
            var curve = HoldOutlineSpline.Densify(Ring(count, 0.02, wobble: 0.2));

            Assert.InRange(curve.Count, count * 3, HoldOutlineSpline.MaxOutlinePoints);
        }
    }

    [Fact]
    public void Curve_IsRounderThanItsControlPolygon()
    {
        var square = Ring(4, 0.02, wobble: 0);

        var curve = HoldOutlineSpline.Densify(square);

        Assert.True(Area(curve) > Area(square) * 1.2);
    }

    [Fact]
    public void RepeatedOrTooFewPoints_ComeBackWithoutACurve()
    {
        var same = Enumerable.Repeat(0, 5).Select(_ => new ShapePoint { Dx = 0.01, Dy = 0.01 }).ToList();
        var two = new List<ShapePoint> { new() { Dx = 0, Dy = 0 }, new() { Dx = 0.01, Dy = 0 } };

        Assert.Equal(5, HoldOutlineSpline.Densify(same).Count);
        Assert.Equal(2, HoldOutlineSpline.Densify(two).Count);
        Assert.All(HoldOutlineSpline.Densify(Ring(6, 0.02, 0.2).Concat(Ring(6, 0.02, 0.2).Take(1)).ToList()), p => Assert.True(double.IsFinite(p.Dx) && double.IsFinite(p.Dy)));
    }

    [Fact]
    public void ADetectedOutlineWithManyPoints_IsReducedToFewControlPoints()
    {
        var detected = Ring(200, 0.02, wobble: 0.15);

        var control = HoldOutlineSpline.ControlPointsFor(detected);

        Assert.InRange(control.Count, 3, HoldOutlineSpline.MaxControlPoints);
        Assert.InRange(Area(HoldOutlineSpline.Densify(control)) / Area(detected), 0.85, 1.15);
        Assert.Equal(8, HoldOutlineSpline.ControlPointsFor(ShapePoint.DefaultOctagon(0.02)).Count);
    }

    [Fact]
    public void SmoothOnAndOff_GivesTheControlPointsBack()
    {
        var hold = new Hold { X = 0.4, Y = 0.3, Radius = 0.02, ShapePoints = ShapePoint.DefaultOctagon(0.02) };

        Assert.True(HoldOutlineSpline.SetSmooth(hold, true));
        Assert.True(HoldOutlineSpline.IsSmooth(hold));
        Assert.Equal(8, hold.ShapeControlPoints!.Count);
        Assert.True(hold.ShapePoints!.Count > 8);
        Assert.False(HoldOutlineSpline.SetSmooth(hold, true));

        Assert.True(HoldOutlineSpline.SetSmooth(hold, false));
        Assert.Null(hold.ShapeControlPoints);
        Assert.Equal(ShapePoint.DefaultOctagon(0.02).Select(p => (p.Dx, p.Dy)), hold.ShapePoints!.Select(p => (p.Dx, p.Dy)));
    }

    [Fact]
    public void AnOutlineRewrittenBehindTheControlPoints_IsNoLongerSmooth()
    {
        var hold = new Hold { X = 0.4, Y = 0.3, Radius = 0.02, ShapePoints = ShapePoint.DefaultOctagon(0.02) };
        HoldOutlineSpline.SetSmooth(hold, true);

        // What a crop, a clean-up or an older version of the app does: it only knows the outline.
        foreach (var p in hold.ShapePoints!)
        {
            p.Dx *= 1.5;
        }

        Assert.False(HoldOutlineSpline.IsSmooth(hold));
        Assert.Null(HoldOutlineSpline.CurrentControlPoints(hold));
    }

    [Fact]
    public async Task Save_OfASmoothOutline_StoresTheCurveAndItsControlPoints_AndReadersSeeTheCurve()
    {
        using var h = new WallTestHarness();
        var hold = (await h.SeedWallAsync(holdCount: 1))[0];
        var control = ShapePoint.DefaultOctagon(hold.Radius);
        var curve = HoldOutlineSpline.Densify(control);

        // The editor sends its drawn outline and the control points; the server draws the curve itself.
        await h.WallService.UpdateHoldAsync(hold.Id, Edit(hold, control, control));

        var stored = await LoadAsync(h, hold.Id);
        Assert.Equal(control.Select(p => (p.Dx, p.Dy)), stored.ShapeControlPoints!.Select(p => (p.Dx, p.Dy)));
        Assert.Equal(curve.Select(p => (p.Dx, p.Dy)), stored.ShapePoints!.Select(p => (p.Dx, p.Dy)));
        Assert.True(HoldOutlineSpline.IsSmooth(stored));

        // The polygon every geometry reader takes, and a working copy for the editor.
        Assert.Equal(curve.Count, HoldShapePolygon.Of(stored).Length);
        Assert.Equal(8, stored.Clone().ShapeControlPoints!.Count);
    }

    [Fact]
    public async Task Save_OfAPlainOutline_DropsOldControlPoints_AndAnUntouchedOutlineKeepsThem()
    {
        using var h = new WallTestHarness();
        var hold = (await h.SeedWallAsync(holdCount: 1))[0];
        var control = ShapePoint.DefaultOctagon(hold.Radius);
        await h.WallService.UpdateHoldAsync(hold.Id, Edit(hold, control, control));

        await h.WallService.UpdateHoldAsync(hold.Id, Edit(hold, null, null));
        Assert.True(HoldOutlineSpline.IsSmooth(await LoadAsync(h, hold.Id)));

        await h.WallService.UpdateHoldAsync(hold.Id, Edit(hold, control, null));
        var plain = await LoadAsync(h, hold.Id);
        Assert.Null(plain.ShapeControlPoints);
        Assert.Equal(8, plain.ShapePoints!.Count);
    }

    private static HoldEdit Edit(Hold hold, List<ShapePoint>? outline, List<ShapePoint>? control) => HoldEdit.FromEditorState(
        hold.X, hold.Y, hold.Radius, hold.Color, hold.Category, hold.IsOnKickboard, outline, hold.Name, hold.Material, hold.HandType,
        flagBouldersOnMove: false, shapeControlPoints: control);

    private static async Task<Hold> LoadAsync(WallTestHarness h, Guid holdId)
    {
        await using var db = h.CreateContext();
        return await db.Holds.AsNoTracking().SingleAsync(x => x.Id == holdId);
    }

    private static List<ShapePoint> Ring(int count, double radius, double wobble) =>
        [.. Enumerable.Range(0, count).Select(i =>
        {
            double angle = 2 * Math.PI * i / count;
            double r = radius * (1 + (wobble * Math.Sin(3 * angle)));
            return new ShapePoint { Dx = Math.Cos(angle) * r, Dy = Math.Sin(angle) * r };
        })];

    private static double Area(IReadOnlyList<ShapePoint> ring)
    {
        double sum = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i];
            var b = ring[(i + 1) % ring.Count];
            sum += (a.Dx * b.Dy) - (b.Dx * a.Dy);
        }

        return Math.Abs(sum) / 2;
    }

    private static bool CrossesItself(IReadOnlyList<ShapePoint> ring)
    {
        int n = ring.Count;
        for (int i = 0; i < n; i++)
        {
            for (int j = i + 2; j < n; j++)
            {
                if (i == 0 && j == n - 1)
                {
                    continue;
                }

                if (Cross(ring[i], ring[(i + 1) % n], ring[j], ring[(j + 1) % n]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool Cross(ShapePoint a, ShapePoint b, ShapePoint c, ShapePoint d) =>
        Side(a, b, c) * Side(a, b, d) < 0 && Side(c, d, a) * Side(c, d, b) < 0;

    private static double Side(ShapePoint a, ShapePoint b, ShapePoint p) =>
        ((b.Dx - a.Dx) * (p.Dy - a.Dy)) - ((b.Dy - a.Dy) * (p.Dx - a.Dx));
}
