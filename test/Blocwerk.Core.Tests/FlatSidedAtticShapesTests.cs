// <copyright file="FlatSidedAtticShapesTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Geometry.Volumes;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The five main-wall volumes of The Attic (flat plywood pyramids and roofs, splat height fields of 2026-09-30) keep
/// their shape: a narrow flat-top reading does not beat the pyramid under it, and a good roof beats a poor flat top with
/// fewer sides.
/// </summary>
public sealed class FlatSidedAtticShapesTests
{
    public static TheoryData<string> Volumes() => new(Load().Select(v => v.Name));

    [Theory]
    [MemberData(nameof(Volumes))]
    public void AtticVolume_GetsItsShape_WithGoodFlatSides(string name)
    {
        var v = Load().Single(x => x.Name == name);

        var fit = FlatSidedFitter.Fit(v.Field, v.Footprint)!;

        Assert.Equal(v.Shape, fit.Polyhedron.Shape);
        Assert.True(fit.IsGood, $"{name}: RMS {fit.RmsMm}");
    }

    [Fact]
    public void Complexity_CountsTopVerticesBeyondAnApex()
    {
        List<(double A, double B)> square = [(0, 0), (400, 0), (400, 400), (0, 400)];
        var pyramid = new VolumePolyhedron(VolumeHull.Build(square, [(200, 200, 80)]).Select(f => f.Vertices));
        var plateau = new VolumePolyhedron(
            VolumeHull.Build(square, [(150, 150, 80), (250, 150, 80), (250, 250, 80), (150, 250, 80)]).Select(f => f.Vertices), "plateau");

        Assert.Equal(4, FlatSidedFitter.Complexity(pyramid));
        Assert.Equal(plateau.SideCount + 3, FlatSidedFitter.Complexity(plateau));
    }

    private static List<(string Name, string Shape, List<(double A, double B)> Footprint, VolumeSurface Field)> Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Volumes", "attic-main-wall-volumes.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        return doc.RootElement.EnumerateArray().Select(e => (
            e.GetProperty("name").GetString()!,
            e.GetProperty("shape").GetString()!,
            e.GetProperty("footprint").EnumerateArray().Select(p => (p[0].GetDouble(), p[1].GetDouble())).ToList(),
            VolumeSurface.FromJson(e.GetProperty("field").GetRawText())!)).ToList();
    }
}
