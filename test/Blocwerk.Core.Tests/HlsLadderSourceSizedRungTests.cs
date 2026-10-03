// <copyright file="HlsLadderSourceSizedRungTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A clip smaller than the whole HLS ladder: one rung at the source's own (even) size, rotation-aware,
/// with a bitrate scaled by pixel count — never the smallest ladder rung, which would upscale it.
/// </summary>
public class HlsLadderSourceSizedRungTests
{
    private static readonly IReadOnlyList<HlsRung> Ladder =
    [
        new(360, 800, 96),
        new(480, 1400, 128),
        new(720, 3000, 128),
    ];

    [Fact]
    public void SourceAtTheSmallestRung_KeepsTheLadderRung()
    {
        var rungs = HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 360, sourceWidth: 640);

        var rung = Assert.Single(rungs);
        Assert.Equal(new HlsRung(360, 800, 96), rung);
        Assert.Null(rung.Width);
    }

    [Fact]
    public void SourceBelowTheLadder_GetsOneRungAtItsOwnSize_WithABitrateScaledByPixels()
    {
        var rung = Assert.Single(HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 180, sourceWidth: 320));

        Assert.Equal(180, rung.Height);
        Assert.Equal(320, rung.Width);
        Assert.Equal(200, rung.VideoKbps); // 800 kbps × (180/360)² — a quarter of the pixels.
        Assert.Equal(96, rung.AudioKbps);
    }

    [Fact]
    public void OddSourceDimensions_RoundDownToEven_SoTheRungNeverExceedsTheSource()
    {
        var rung = Assert.Single(HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 181, sourceWidth: 321));

        Assert.Equal(180, rung.Height);
        Assert.Equal(320, rung.Width);
    }

    [Fact]
    public void RotatedSource_SizesTheRungFromTheDisplayedAxes()
    {
        // A 320×180 coded clip shot in portrait (rotation 90) displays as 180×320: 320 tall, under 360.
        var height = HlsLadderPlanner.DisplayedHeight(320, 180, 90);
        var width = HlsLadderPlanner.DisplayedWidth(320, 180, 90);

        var rung = Assert.Single(HlsLadderPlanner.SelectRungs(Ladder, height, width));

        Assert.Equal(320, rung.Height);
        Assert.Equal(180, rung.Width);
    }

    [Fact]
    public void AVeryTinySource_KeepsABitrateFloor()
    {
        var rung = Assert.Single(HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 64, sourceWidth: 114));

        Assert.Equal(HlsLadderPlanner.MinVideoKbps, rung.VideoKbps);
        Assert.Equal(114, rung.Width);
    }

    [Fact]
    public void UnknownWidth_LetsFfmpegDeriveIt()
    {
        var rung = Assert.Single(HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 240));

        Assert.Null(rung.Width);
        Assert.Contains("scale=-2:240", HlsLadderPlanner.BuildArguments("/in.mp4", "/out", [rung], 4, hasAudio: false));
    }

    [Fact]
    public void BuildArguments_ScalesASourceSizedRungToItsExactWidth()
    {
        var rungs = HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 180, sourceWidth: 320);

        var args = HlsLadderPlanner.BuildArguments("/in.mp4", "/out", rungs, 4, hasAudio: true);

        Assert.Contains("split=1", args);
        Assert.Contains("scale=320:180", args);
        Assert.Contains("-b:v:0 200k", args);
        Assert.Contains("v:0,a:0", args);
    }
}
