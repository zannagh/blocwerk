// <copyright file="TextureSourceMapTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests;

/// <summary>The worker's source-view map (sourcemap.py): cells over the facet plane naming the painting photo.</summary>
public class TextureSourceMapTests
{
    [Fact]
    public void Parse_LooksUpTheCameraPerCell()
    {
        var map = TextureSourceMap.Parse(Doc())!;

        Assert.Null(map.CameraAt(5, 15));
        Assert.Equal("A", map.CameraAt(15, 15));
        Assert.Equal("B", map.CameraAt(25, 11));
        Assert.Equal("A", map.CameraAt(1, 1));
        Assert.Null(map.CameraAt(-1, 1));
        Assert.Null(map.CameraAt(5, 21));
    }

    [Fact]
    public void Dominant_PicksTheCameraCoveringMostPoints()
    {
        var map = TextureSourceMap.Parse(Doc())!;

        Assert.Equal("A", map.Dominant([(25, 15), (15, 15), (5, 5), (15, 5)]));
        Assert.Null(map.Dominant([(5, 15), (100, 100)]));
    }

    [Fact]
    public void Parse_Reads16BitCells()
    {
        // 6 little-endian uint16: 0, 1, 2, 1, 1, 0
        var raw = Convert.ToBase64String([0, 0, 1, 0, 2, 0, 1, 0, 1, 0, 0, 0]);
        Assert.Equal("B", TextureSourceMap.Parse(Doc(16, raw))!.CameraAt(25, 15));
    }

    [Theory]
    [InlineData(8, "AAECAQ==", 1)] // too few cells
    [InlineData(8, "AAMCAQEA", 1)] // camera index 3 of 2
    [InlineData(8, "AAECAQEA", 2)] // unknown version
    [InlineData(12, "AAECAQEA", 1)] // bad bit depth
    [InlineData(8, "not base64!", 1)]
    public void Parse_RejectsMalformedMaps(int bits, string cells, int version)
    {
        Assert.Null(TextureSourceMap.Parse(Doc(bits, cells, version)));
        Assert.Null(TextureSourceMap.Parse(Encoding.UTF8.GetBytes("[1,2]")));
        Assert.Null(TextureSourceMap.Parse(Encoding.UTF8.GetBytes("{")));
    }

    /// <summary>2 × 3 cells of 10 mm from a = 0, b = 20 down: row 0 [-, A, B], row 1 [A, A, -].</summary>
    internal static byte[] Doc(int bits = 8, string cells = "AAECAQEA", int version = 1) => Encoding.UTF8.GetBytes(
        $$"""{"version":{{version}},"cellMm":10,"aMin":0,"bMax":20,"cols":3,"rows":2,"cameras":["A","B"],"bits":{{bits}},"cells":"{{cells}}"}""");
}
