// <copyright file="HoldOutlineSplineFixtureTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Runtime.CompilerServices;
using System.Text.Json;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Pins <see cref="HoldOutlineSpline.Densify"/> to fixed inputs and outputs. The browser redraws a smooth outline
/// with its own copy of the curve (wwwroot/js/shape-geometry.js) while a control point is dragged, and the editor
/// stores what the server computes; the two must agree to the last digit or the outline jumps on release.
/// test/js/shape-tools.test.mjs checks the JS copy against the SAME fixture file.
/// Regenerate (after a deliberate change to the curve) with BLOCWERK_WRITE_SPLINE_FIXTURE=1.
/// </summary>
public class HoldOutlineSplineFixtureTests
{
    [Fact]
    public void Densify_GivesTheFixturesOutputs_Exactly()
    {
        var cases = Cases();
        if (Environment.GetEnvironmentVariable("BLOCWERK_WRITE_SPLINE_FIXTURE") == "1")
        {
            Write(cases);
        }

        var stored = JsonSerializer.Deserialize<List<FixtureCase>>(File.ReadAllText(FixturePath()))!;
        Assert.Equal(cases.Count, stored.Count);
        for (int c = 0; c < cases.Count; c++)
        {
            Assert.Equal(cases[c].Name, stored[c].Name);
            Assert.Equal(Flatten(cases[c].Control), stored[c].Input);
            Assert.Equal(stored[c].Output, Flatten(HoldOutlineSpline.Densify(cases[c].Control)));
        }
    }

    [Fact]
    public void Fixture_CoversEveryControlPointCount_TheEditorOffers()
    {
        var counts = Cases().Select(c => c.Control.Count).ToHashSet();

        Assert.Contains(3, counts);
        Assert.Contains(HoldOutlineSpline.MaxControlPoints, counts);
        Assert.Contains(HoldOutlineSpline.MaxEditableControlPoints, counts);
    }

    private static List<(string Name, List<ShapePoint> Control)> Cases()
    {
        var random = new Random(20261020);
        return
        [
            ("triangle", Points((0.01, 0), (-0.005, 0.0087), (-0.005, -0.0087))),
            ("square", Points((-0.01, -0.01), (0.01, -0.01), (0.01, 0.01), (-0.01, 0.01))),
            ("uneven-ring-8", Ring(8, 0.02, 0.35, random)),
            ("uneven-ring-13", Ring(13, 0.015, 0.4, random)),
            ("max-16", Ring(HoldOutlineSpline.MaxControlPoints, 0.03, 0.25, random)),
            ("editable-32", Ring(HoldOutlineSpline.MaxEditableControlPoints, 0.025, 0.3, random)),
            ("repeated-points", Points((0, 0), (0, 0), (0.012, 0.001), (0.01, 0.014), (0.01, 0.014), (-0.003, 0.009), (0, 0))),
            ("two-distinct", Points((0.004, 0.002), (0.004, 0.002), (-0.006, 0.001))),
            ("long-thin", Points((-0.04, -0.001), (0.04, -0.0012), (0.041, 0.0015), (-0.039, 0.001))),
        ];
    }

    private static List<ShapePoint> Points(params (double X, double Y)[] points) => [.. points.Select(p => new ShapePoint { Dx = p.X, Dy = p.Y })];

    private static List<ShapePoint> Ring(int count, double radius, double wobble, Random random) =>
        [.. Enumerable.Range(0, count).Select(i =>
        {
            double angle = (2 * Math.PI * (i + (random.NextDouble() * 0.4))) / count;
            double r = radius * (1 + (wobble * ((random.NextDouble() * 2) - 1)));
            return new ShapePoint { Dx = Math.Round(r * Math.Cos(angle), 6), Dy = Math.Round(r * Math.Sin(angle) * 0.8, 6) };
        })];

    private static double[] Flatten(IEnumerable<ShapePoint> points) => [.. points.SelectMany(p => new[] { p.Dx, p.Dy })];

    private static void Write(List<(string Name, List<ShapePoint> Control)> cases)
    {
        var fixture = cases.Select(c => new FixtureCase(c.Name, Flatten(c.Control), Flatten(HoldOutlineSpline.Densify(c.Control)))).ToList();
        File.WriteAllText(FixturePath(), JsonSerializer.Serialize(fixture, new JsonSerializerOptions { WriteIndented = true }) + "\n");
    }

    private static string FixturePath([CallerFilePath] string thisFile = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "js", "fixtures", "spline-densify.json"));

    /// <summary>One fixture case: control points and the curve, both flattened (dx0, dy0, dx1, dy1, ...).</summary>
    /// <param name="Name">The case.</param>
    /// <param name="Input">The control points.</param>
    /// <param name="Output">The curve.</param>
    private sealed record FixtureCase(string Name, double[] Input, double[] Output);
}
