// <copyright file="BetaVideoMetadataScrubTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Every beta-clip output (remux, transcode, HLS ladder) drops the phone's recording location and
/// other identifying metadata. Verified by hand with ffprobe (ffmpeg 9): a clip carrying <c>location</c>
/// and <c>com.apple.quicktime.location.ISO6709</c> came out with neither tag nor a <c>loci</c> atom,
/// and the remux kept its 90° display matrix. These pin the argument shape without ffmpeg on the box.
/// </summary>
public class BetaVideoMetadataScrubTests
{
    public static TheoryData<string> Outputs()
    {
        var ladder = new List<HlsRung> { new(360, 800, 96), new(720, 2500, 128) };
        return new TheoryData<string>
        {
            BetaVideoArguments.Remux("/in.mov", "/out.mp4"),
            BetaVideoArguments.Transcode("/in.mov", "/out.mp4", 2000),
            HlsLadderPlanner.BuildArguments("/in.mov", "/out", HlsLadderPlanner.SelectRungs(ladder, 720), 4, hasAudio: true, rotationDegrees: 90),
        };
    }

    [Theory]
    [MemberData(nameof(Outputs))]
    public void EveryOutput_ScrubsGlobalAndPerStreamMetadata_AfterTheInput(string args)
    {
        Assert.Contains("-map_metadata -1 ", args);
        Assert.Contains("-map_metadata:s:v -1", args);
        Assert.Contains("-map_metadata:s:a -1", args);
        Assert.Contains("-map_chapters -1", args);
        Assert.Contains(" -dn ", args);
        Assert.True(
            args.IndexOf("-map_metadata", StringComparison.Ordinal) > args.IndexOf("-i ", StringComparison.Ordinal),
            "The scrub is an OUTPUT option, so it must follow -i.");
    }

    [Fact]
    public void Remux_StillStreamCopies_SoTheDisplayMatrixSurvives()
    {
        var args = BetaVideoArguments.Remux("/in.mov", "/out.mp4");

        Assert.Contains("-c copy", args);
        Assert.DoesNotContain("-noautorotate", args);
        Assert.DoesNotContain("rotate=", args);
        Assert.EndsWith("\"/out.mp4\"", args);
    }

    [Fact]
    public void Transcode_KeepsTheWebSafeProfile()
    {
        var args = BetaVideoArguments.Transcode("/in.mov", "/out.mp4", 2000);

        Assert.Contains("-b:v 2000k -maxrate 3000k -bufsize 4000k", args);
        Assert.Contains("-movflags +faststart", args);
        Assert.Contains("-pix_fmt yuv420p", args);
    }

    [Fact]
    public void Transcode_ScalesDownOnly_ClampingTheBoxToTheFrameItself()
    {
        var args = BetaVideoArguments.Transcode("/in.mov", "/out.mp4", 2000);

        // The box is min(cap, own size), so a 640x360 clip keeps its size instead of filling 1280x1280.
        Assert.Contains($"-vf \"{BetaVideoArguments.DownscaleOnlyFilter}\"", args);
        Assert.Contains("w='min(1280,iw)'", BetaVideoArguments.DownscaleOnlyFilter);
        Assert.Contains("h='min(1280,ih)'", BetaVideoArguments.DownscaleOnlyFilter);
        Assert.Contains("force_original_aspect_ratio=decrease", BetaVideoArguments.DownscaleOnlyFilter);
        Assert.Contains("force_divisible_by=2", BetaVideoArguments.DownscaleOnlyFilter);
        Assert.DoesNotContain("w=1280:h=1280", args);
    }

    [Fact]
    public void Transcode_LeavesRotationToAutorotate_SoPortraitStaysPortrait()
    {
        var args = BetaVideoArguments.Transcode("/in.mov", "/out.mp4", 2000);

        // iw/ih in the scale are the upright size only while ffmpeg's autorotate runs first.
        Assert.DoesNotContain("-noautorotate", args);
        Assert.DoesNotContain("transpose", args);
        Assert.True(
            args.IndexOf("-vf", StringComparison.Ordinal) > args.IndexOf("-i ", StringComparison.Ordinal),
            "The scale is an output filter, applied after the input's display matrix.");
    }
}
