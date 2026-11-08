// <copyright file="ArucoDictionaryTableTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.MarkerPlanning;
using Blocwerk.HoldDetection.Markers;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Tests.MarkerPlanning;

/// <summary>
/// The planner's constant DICT_4X4_50 table (pure C#, used to draw printable markers) must match
/// OpenCV's own <c>generateImageMarker</c> module for module, for every id.
/// </summary>
public class ArucoDictionaryTableTests
{
    private const int ModulePx = 10;

    [Fact]
    public void Table_MatchesOpenCv_ForAll50Ids()
    {
        for (var id = 0; id < ArucoDict4X4.Count; id++)
        {
            using var marker = ArucoInterop.GenerateMarker(id, ArucoDict4X4.ModulesPerSide * ModulePx);
            for (var row = 0; row < ArucoDict4X4.ModulesPerSide; row++)
            {
                for (var col = 0; col < ArucoDict4X4.ModulesPerSide; col++)
                {
                    var white = marker.At<byte>((row * ModulePx) + (ModulePx / 2), (col * ModulePx) + (ModulePx / 2)) > 127;
                    Assert.True(
                        white == ArucoDict4X4.IsWhite(id, row, col),
                        $"id {id} module ({row}, {col}): OpenCV says {(white ? "white" : "black")}");
                }
            }
        }
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(50)]
    public void IdsOutsideTheDictionary_Throw(int id)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ArucoDict4X4.IsWhite(id, 1, 1));
    }
}
