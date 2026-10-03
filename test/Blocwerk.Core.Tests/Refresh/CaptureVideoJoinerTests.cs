// <copyright file="CaptureVideoJoinerTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Refresh;

namespace Blocwerk.Core.Tests.Refresh;

/// <summary>Several videos are joined without re-encoding only when their streams match; otherwise the longest one is used.</summary>
public class CaptureVideoJoinerTests
{
    [Fact]
    public void MatchingStreams_CanBeJoined()
    {
        var a = CaptureVideoJoiner.Parse("a.mov", Probe("hevc", 3840, 2160, "30/1", -90, 41.2));
        var b = CaptureVideoJoiner.Parse("b.mov", Probe("hevc", 3840, 2160, "30/1", -90, 12.5));

        Assert.True(CaptureVideoJoiner.CanJoin([a, b]));
        Assert.Equal(41.2, a.DurationSeconds, 3);
    }

    [Theory]
    [InlineData("h264", 3840, 2160, "30/1", -90)]
    [InlineData("hevc", 1920, 1080, "30/1", -90)]
    [InlineData("hevc", 3840, 2160, "60/1", -90)]
    [InlineData("hevc", 3840, 2160, "30/1", 0)]
    public void DifferentStreams_CannotBeJoined(string codec, int width, int height, string rate, int rotation)
    {
        var a = CaptureVideoJoiner.Parse("a.mov", Probe("hevc", 3840, 2160, "30/1", -90, 10));
        var b = CaptureVideoJoiner.Parse("b.mov", Probe(codec, width, height, rate, rotation, 10));

        Assert.False(CaptureVideoJoiner.CanJoin([a, b]));
    }

    [Fact]
    public void NoVideoStream_CannotBeJoined()
    {
        var empty = CaptureVideoJoiner.Parse("x.mov", "{\"streams\":[]}");

        Assert.False(CaptureVideoJoiner.CanJoin([empty, empty]));
    }

    [Fact]
    public void ConcatList_QuotesAnApostropheInThePath()
    {
        Assert.Equal(@"file '/data/it'\''s walk.mov'", CaptureVideoJoiner.ConcatLine("/data/it's walk.mov"));
    }

    private static string Probe(string codec, int width, int height, string rate, int rotation, double duration) =>
        $$$"""
        {"streams":[{"codec_name":"{{{codec}}}","profile":"Main 10","width":{{{width}}},"height":{{{height}}},"pix_fmt":"yuv420p10le",
          "r_frame_rate":"{{{rate}}}","side_data_list":[{"rotation":{{{rotation}}}}]}],
         "format":{"duration":"{{{duration.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"}}
        """;
}
