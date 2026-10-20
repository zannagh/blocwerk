// <copyright file="ShapeEditValidationTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The server's check on what the wall editor's browser-side Shape tool sends (a dragged point, an eraser or a brush
/// stroke): <see cref="ShapeEditValidation"/>.
/// </summary>
public class ShapeEditValidationTests
{
    [Fact]
    public void Validation_TakesAPlainOutline()
    {
        Assert.Null(ShapeEditValidation.Problem(0.5, 0.5, Square(0.01), control: false, requireSimple: true));
    }

    [Fact]
    public void Validation_RefusesFewerThanThreePoints()
    {
        Assert.NotNull(ShapeEditValidation.Problem(0.5, 0.5, Square(0.01).Take(2).ToList(), control: false, requireSimple: false));
    }

    [Fact]
    public void Validation_RefusesACrossingOutline_OnlyWhereItMust()
    {
        var bowTie = Points((-0.01, -0.01), (0.02, 0.01), (0.02, -0.01), (-0.01, 0.03));

        Assert.Equal("The outline would cross itself.", ShapeEditValidation.Problem(0.5, 0.5, bowTie, control: false, requireSimple: true));

        // The check itself can be asked without it; the editor asks with it for every kind of edit (a dragged point too).
        Assert.Null(ShapeEditValidation.Problem(0.5, 0.5, bowTie, control: false, requireSimple: false));
    }

    [Fact]
    public void Validation_RefusesAnOutlineOffThePhoto_AndInvalidNumbers()
    {
        Assert.NotNull(ShapeEditValidation.Problem(0.995, 0.5, Square(0.02), control: false, requireSimple: false));
        Assert.Null(ShapeEditValidation.Problem(0.995, 0.5, Square(0.005), control: false, requireSimple: false));

        var bad = Square(0.01);
        bad[1].Dx = double.NaN;
        Assert.NotNull(ShapeEditValidation.Problem(0.5, 0.5, bad, control: false, requireSimple: false));
    }

    [Fact]
    public void Validation_RefusesTooManyPoints_AndNoArea()
    {
        var many = Enumerable.Range(0, ShapeEditValidation.MaxOutlinePoints + 1)
            .Select(i => new ShapePoint { Dx = 0.01 * Math.Cos(i * 0.01), Dy = 0.01 * Math.Sin(i * 0.01) }).ToList();
        Assert.NotNull(ShapeEditValidation.Problem(0.5, 0.5, many, control: false, requireSimple: false));
        Assert.NotNull(ShapeEditValidation.Problem(0.5, 0.5, Points((0, 0), (0.01, 0), (0.02, 0)), control: false, requireSimple: false));
    }

    [Fact]
    public void Validation_ChecksTheCurve_OfControlPoints()
    {
        var control = Square(0.01);

        Assert.Null(ShapeEditValidation.Problem(0.5, 0.5, control, control: true, requireSimple: true));
        var tooMany = Enumerable.Range(0, HoldOutlineSpline.MaxEditableControlPoints + 1)
            .Select(i => new ShapePoint { Dx = 0.01 * Math.Cos(i * 0.2), Dy = 0.01 * Math.Sin(i * 0.2) }).ToList();
        Assert.NotNull(ShapeEditValidation.Problem(0.5, 0.5, tooMany, control: true, requireSimple: true));
    }

    private static List<ShapePoint> Square(double r) => Points((-r, -r), (r, -r), (r, r), (-r, r));

    private static List<ShapePoint> Points(params (double X, double Y)[] points) => [.. points.Select(p => new ShapePoint { Dx = p.X, Dy = p.Y })];
}
