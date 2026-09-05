using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The pure HLS backend helpers the serving endpoint and transcoder lean on: ladder selection (capped
/// to the source height, never upscaled), the share-token playlist rewrite, and the path-traversal
/// guard. Kept here — free of ffmpeg, a web host and the disk — so the security-critical bits are
/// pinned down directly.
/// </summary>
public class HlsBackendTests
{
    private static readonly IReadOnlyList<HlsRung> Ladder =
    [
        new(360, 800, 96),
        new(480, 1400, 128),
        new(720, 3000, 128),
        new(1080, 6000, 192),
    ];

    [Fact]
    public void SelectRungs_CapsToSourceHeight_NeverUpscales()
    {
        var rungs = HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 720);

        Assert.Equal([360, 480, 720], rungs.Select(r => r.Height));
    }

    [Fact]
    public void SelectRungs_IncludesEveryRung_ForA1080pSource()
    {
        var rungs = HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 1080);

        Assert.Equal([360, 480, 720, 1080], rungs.Select(r => r.Height));
    }

    [Fact]
    public void SelectRungs_ForATinySource_KeepsOnlyTheSmallestRung()
    {
        // A source shorter than the smallest rung must still get exactly one rung, and never one taller
        // than itself would be upscaled to — the smallest is the safe floor.
        var rungs = HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 240);

        Assert.Equal([360], rungs.Select(r => r.Height));
    }

    [Fact]
    public void SelectRungs_ForAnUnknownHeight_KeepsOnlyTheSmallestRung()
    {
        var rungs = HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 0);

        Assert.Equal([360], rungs.Select(r => r.Height));
    }

    [Fact]
    public void BuildArguments_HasTheExpectedHlsMuxerShape()
    {
        var rungs = HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 480);
        var args = HlsLadderPlanner.BuildArguments("/in.mp4", "/out", rungs, segmentSeconds: 4, hasAudio: true);

        Assert.Contains("-filter_complex", args);
        Assert.Contains("split=2", args);
        Assert.Contains("scale=-2:360", args);
        Assert.Contains("scale=-2:480", args);
        Assert.Contains("-var_stream_map", args);
        Assert.Contains("v:0,a:0 v:1,a:1", args);
        Assert.Contains("-master_pl_name master.m3u8", args);
        Assert.Contains("-hls_time 4", args);
        Assert.Contains("-hls_flags independent_segments", args);
        Assert.Contains("-sc_threshold:v:0 0", args);
    }

    [Fact]
    public void BuildArguments_DisablesAutoRotation_BeforeTheInput()
    {
        // Modern ffmpeg auto-rotates a [0:v] stream feeding a complex graph, which would double-rotate
        // on top of our explicit transpose (verified on prod ffmpeg 6.1.1: a portrait clip came out
        // sideways without this). -noautorotate must be an INPUT option, i.e. sit before -i.
        var rungs = HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 480);
        var args = HlsLadderPlanner.BuildArguments(
            "/in.mp4", "/out", rungs, segmentSeconds: 4, hasAudio: true, rotationDegrees: 90);

        Assert.Contains("-noautorotate", args);
        Assert.True(
            args.IndexOf("-noautorotate", StringComparison.Ordinal) < args.IndexOf("-i ", StringComparison.Ordinal),
            "-noautorotate must precede -i so it is applied as an input option.");
    }

    [Fact]
    public void DisplayedHeight_ForA90DegPortrait_SwapsToTheCodedWidth()
    {
        // A phone clip coded 1920x1080 but shot in portrait displays as 1080x1920: the displayed height
        // is the coded WIDTH, so the ladder must cap at 1920 (all rungs), not the coded 1080.
        var displayed = HlsLadderPlanner.DisplayedHeight(codedWidth: 1920, codedHeight: 1080, rotationDegrees: 90);
        Assert.Equal(1920, displayed);

        var rungs = HlsLadderPlanner.SelectRungs(Ladder, displayed);
        Assert.Equal([360, 480, 720, 1080], rungs.Select(r => r.Height));
    }

    [Fact]
    public void DisplayedHeight_For0And180_KeepsCodedHeight_And270SwapsToCodedWidth()
    {
        Assert.Equal(1080, HlsLadderPlanner.DisplayedHeight(1920, 1080, 0));
        Assert.Equal(1080, HlsLadderPlanner.DisplayedHeight(1920, 1080, 180));
        Assert.Equal(1920, HlsLadderPlanner.DisplayedHeight(1920, 1080, 270));
    }

    [Theory]
    [InlineData(0, "[0:v]split=2")]
    [InlineData(90, "[0:v]transpose=1,split=2")]
    [InlineData(270, "[0:v]transpose=2,split=2")]
    [InlineData(180, "[0:v]transpose=1,transpose=1,split=2")]
    public void BuildArguments_EmitsThePreSplitRotationFilter_ForEachRotation(int rotation, string expected)
    {
        var rungs = HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 480);
        var args = HlsLadderPlanner.BuildArguments(
            "/in.mp4", "/out", rungs, segmentSeconds: 4, hasAudio: true, rotationDegrees: rotation);

        Assert.Contains(expected, args);
        if (rotation == 0)
        {
            Assert.DoesNotContain("transpose", args);
        }

        // The scale stage still targets the displayed rung heights after the rotation.
        Assert.Contains("scale=-2:360", args);
        Assert.Contains("scale=-2:480", args);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(90, true)]
    [InlineData(180, true)]
    [InlineData(270, true)]
    [InlineData(HlsLadderPlanner.UnhandledRotation, false)]
    [InlineData(45, false)]
    [InlineData(-90, false)]
    public void IsSupportedRotation_OnlyAcceptsCleanQuarterTurns(int rotation, bool supported)
    {
        Assert.Equal(supported, HlsLadderPlanner.IsSupportedRotation(rotation));
    }

    [Fact]
    public void AppendToken_RewritesTheUriInsideMapMediaAndKeyTags_ButLeavesOtherTags()
    {
        const string playlist =
            "#EXTM3U\n" +
            "#EXT-X-MAP:URI=\"init.mp4\"\n" +
            "#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin?v=2\"\n" +
            "#EXT-X-MEDIA:TYPE=AUDIO,URI=\"audio/eng.m3u8\"\n" +
            "#EXT-X-STREAM-INF:BANDWIDTH=800000\n" +
            "v0.m3u8\n";

        var rewritten = HlsPlaylistRewriter.AppendToken(playlist, "abc123");

        Assert.Contains("#EXT-X-MAP:URI=\"init.mp4?token=abc123\"", rewritten);
        Assert.Contains("#EXT-X-KEY:METHOD=AES-128,URI=\"key.bin?v=2&token=abc123\"", rewritten);
        Assert.Contains("#EXT-X-MEDIA:TYPE=AUDIO,URI=\"audio/eng.m3u8?token=abc123\"", rewritten);
        Assert.Contains("v0.m3u8?token=abc123", rewritten);

        // A tag without a URI attribute is still left completely alone.
        Assert.Contains("#EXT-X-STREAM-INF:BANDWIDTH=800000\n", rewritten);
        Assert.DoesNotContain("#EXTM3U?token", rewritten);
    }

    [Fact]
    public void BuildArguments_WithoutAudio_MapsNoAudioStreams()
    {
        var rungs = HlsLadderPlanner.SelectRungs(Ladder, sourceHeight: 360);
        var args = HlsLadderPlanner.BuildArguments("/in.mp4", "/out", rungs, segmentSeconds: 6, hasAudio: false);

        Assert.DoesNotContain("-map a:0", args);
        Assert.DoesNotContain(",a:0", args);
        Assert.Contains("-hls_time 6", args);
    }

    [Fact]
    public void AppendToken_AddsTheTokenToUriLines_AndLeavesTagsAndBlanksAlone()
    {
        const string playlist = "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000\nv0.m3u8\n\nv1.m3u8\n";

        var rewritten = HlsPlaylistRewriter.AppendToken(playlist, "abc123");

        Assert.Contains("v0.m3u8?token=abc123", rewritten);
        Assert.Contains("v1.m3u8?token=abc123", rewritten);
        Assert.Contains("#EXTM3U\n", rewritten);
        Assert.Contains("#EXT-X-STREAM-INF:BANDWIDTH=800000\n", rewritten);

        // Tag lines must not have picked up a token.
        Assert.DoesNotContain("#EXT-X-STREAM-INF:BANDWIDTH=800000?token", rewritten);
        Assert.DoesNotContain("#EXTM3U?token", rewritten);
    }

    [Fact]
    public void AppendToken_UrlEncodesTheToken_AndUsesAmpersandWhenAQueryExists()
    {
        var rewritten = HlsPlaylistRewriter.AppendToken("seg.ts\nseg2.ts?x=1\n", "a b&c");

        Assert.Contains("seg.ts?token=a%20b%26c", rewritten);
        Assert.Contains("seg2.ts?x=1&token=a%20b%26c", rewritten);
    }

    [Fact]
    public void AppendToken_WithNoToken_ReturnsThePlaylistUnchanged()
    {
        const string playlist = "#EXTM3U\nv0.m3u8\n";

        Assert.Equal(playlist, HlsPlaylistRewriter.AppendToken(playlist, string.Empty));
    }

    [Fact]
    public void Resolve_AllowsAPlaylistOrSegmentInsideTheDirectory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hlsroot");

        Assert.NotNull(HlsPathResolver.Resolve(dir, "master.m3u8"));
        Assert.NotNull(HlsPathResolver.Resolve(dir, "v0_000.ts"));
        Assert.NotNull(HlsPathResolver.Resolve(dir, "v0.m4s"));
    }

    [Theory]
    [InlineData("../secret.ts")]
    [InlineData("../../etc/passwd.ts")]
    [InlineData("sub/../../escape.ts")]
    [InlineData("nested/deep/../../../out.ts")]
    public void Resolve_RejectsTraversalEscapes(string path)
    {
        var dir = Path.Combine(Path.GetTempPath(), "hlsroot");

        Assert.Null(HlsPathResolver.Resolve(dir, path));
    }

    [Fact]
    public void Resolve_RejectsAbsoluteAndRootedPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hlsroot");

        Assert.Null(HlsPathResolver.Resolve(dir, "/etc/passwd.ts"));
        Assert.Null(HlsPathResolver.Resolve(dir, "/tmp/other.m3u8"));
    }

    [Fact]
    public void Resolve_RejectsUnexpectedExtensions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hlsroot");

        Assert.Null(HlsPathResolver.Resolve(dir, "master.m3u8.exe"));
        Assert.Null(HlsPathResolver.Resolve(dir, "config.json"));
        Assert.Null(HlsPathResolver.Resolve(dir, "noext"));
    }

    [Fact]
    public void Resolve_RejectsBackslashesAndEmptyPaths()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hlsroot");

        Assert.Null(HlsPathResolver.Resolve(dir, "..\\escape.ts"));
        Assert.Null(HlsPathResolver.Resolve(dir, string.Empty));
        Assert.Null(HlsPathResolver.Resolve(dir, "   "));
    }
}
