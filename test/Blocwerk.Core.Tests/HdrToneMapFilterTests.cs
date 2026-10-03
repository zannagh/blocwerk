// <copyright file="HdrToneMapFilterTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// HDR (iPhone HLG / PQ) capture videos are tone-mapped to SDR BT.709 during frame extraction: the
/// pure filter choice per ffmpeg build, the filter-list parsing, the probe's colour tags, the ffmpeg
/// arguments, and (where ffmpeg is installed) a real HLG-tagged clip.
/// </summary>
public class HdrToneMapFilterTests : IDisposable
{
    private static readonly HashSet<string> Ubuntu = ["zscale", "tonemap", "libplacebo", "colorspace", "scale"];
    private static readonly HashSet<string> Homebrew = ["tonemap", "colorspace", "scale", HdrToneMapFilter.ScaleColorManagement];

    private readonly string dir = Directory.CreateTempSubdirectory("blocwerk-hdr-test-").FullName;

    [Theory]
    [InlineData("arib-std-b67")]
    [InlineData("smpte2084")]
    [InlineData("ARIB-STD-B67")]
    public void Hdr_PrefersZscale(string transfer) =>
        Assert.Equal(HdrToneMapFilter.ZscaleChain, HdrToneMapFilter.Select(transfer, Ubuntu));

    [Fact]
    public void Hdr_WithoutZscale_UsesSwscaleColourManagement() =>
        Assert.Equal(HdrToneMapFilter.ScaleChain, HdrToneMapFilter.Select(HdrToneMapFilter.Hlg, Homebrew));

    [Fact]
    public void Hdr_WithOnlyColorspace_FallsBackToIt_AndNeverToLibplacebo()
    {
        Assert.Equal(HdrToneMapFilter.ColorspaceChain, HdrToneMapFilter.Select(HdrToneMapFilter.Pq, new HashSet<string> { "colorspace", "libplacebo" }));
        Assert.Null(HdrToneMapFilter.Select(HdrToneMapFilter.Hlg, new HashSet<string> { "libplacebo", "tonemap" }));
        Assert.Null(HdrToneMapFilter.Select(HdrToneMapFilter.Hlg, new HashSet<string> { "zscale" })); // zscale alone cannot tone-map
    }

    [Theory]
    [InlineData("bt709")]
    [InlineData("smpte170m")]
    [InlineData("unknown")]
    [InlineData(null)]
    public void Sdr_GetsNoToneMap(string? transfer)
    {
        Assert.False(HdrToneMapFilter.IsHdr(transfer));
        Assert.Null(HdrToneMapFilter.Select(transfer, Ubuntu));
    }

    [Fact]
    public void Catalog_ParsesFilterRows_AndSwscaleColourManagement()
    {
        const string filters = """
            Filters:
              T.. = Timeline support
              A = Audio input/output
              | = Source or sink filter
              ------
             TS. colorspace        V->V       Convert between colorspaces.
             ..C libplacebo        N->V       Apply various GPU filters from libplacebo
             .SC zscale            V->V       Apply resizing, colorspace and bit depth conversion.
             ... testsrc2          |->V       Generate another test pattern.
            """;
        var names = FfmpegFilterCatalog.Parse(filters, "  out_transfer  <int> ...\n     perceptual  0  perceptual tone mapping");
        Assert.True(
            names.SetEquals(["colorspace", "libplacebo", "zscale", "testsrc2", HdrToneMapFilter.ScaleColorManagement]),
            string.Join(",", names));
        Assert.DoesNotContain(HdrToneMapFilter.ScaleColorManagement, FfmpegFilterCatalog.Parse(filters, "  in_color_matrix <int>"));
    }

    [Fact]
    public void ParseProbe_ReadsTheColourTags()
    {
        const string json = """
            {"streams":[{"width":2160,"height":3840,"color_transfer":"arib-std-b67","color_primaries":"bt2020","color_space":"bt2020nc"}],"format":{"duration":"101.8"}}
            """;
        var probe = CaptureVideoFrameExtractor.ParseProbe(json)!;
        Assert.Equal(("arib-std-b67", "bt2020", "bt2020nc"), (probe.ColorTransfer, probe.ColorPrimaries, probe.ColorSpace));
        Assert.Null(CaptureVideoFrameExtractor.ParseProbe("""{"streams":[{"width":2,"height":2,"color_transfer":"unknown"}],"format":{"duration":"1"}}""")!.ColorTransfer);
    }

    [Fact]
    public void FfmpegArguments_AppendTheToneMapAfterTheDownscale_OnlyWhenGiven()
    {
        var hdr = VideoFilter(HdrToneMapFilter.ZscaleChain);
        Assert.EndsWith("force_original_aspect_ratio=decrease," + HdrToneMapFilter.ZscaleChain, hdr, StringComparison.Ordinal);
        Assert.Contains("tonemap=", hdr, StringComparison.Ordinal);

        var sdr = VideoFilter(null);
        Assert.EndsWith("force_original_aspect_ratio=decrease", sdr, StringComparison.Ordinal);
        Assert.DoesNotContain("tonemap", sdr, StringComparison.Ordinal);
        Assert.DoesNotContain("transfer", sdr, StringComparison.Ordinal);
    }

    [SkippableFact]
    public async Task HlgTaggedClip_IsProbedAsHdr_AndItsFramesAreToneMapped()
    {
        Skip.IfNot(FfmpegAvailable(), "ffmpeg is not installed");
        var filters = await FfmpegFilterCatalog.GetAsync("ffmpeg", CancellationToken.None);
        Skip.If(HdrToneMapFilter.Select(HdrToneMapFilter.Hlg, filters) is null, "this ffmpeg has no tone-mapping filter");
        var clip = Path.Combine(dir, "hlg.mp4");
        await RunFfmpegAsync("-y", "-v", "error", "-f", "lavfi", "-i", "color=c=0x6080c0:s=320x240:d=2:r=10",
            "-vf", "setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc:range=tv",
            "-c:v", "mpeg4", "-q:v", "2", clip);
        var extractor = new CaptureVideoFrameExtractor(new BlocwerkSettings());

        var probe = await extractor.ProbeAsync(clip, CancellationToken.None);
        var frames = await extractor.ExtractAsync(clip, new CaptureVideoFrameRequest(1, 2, TimeSpan.FromMinutes(1)), null, CancellationToken.None);

        Assert.Equal(HdrToneMapFilter.Hlg, probe.ColorTransfer);
        Assert.NotEmpty(frames);
        using var bitmap = SKBitmap.Decode(frames[0]);
        var centre = bitmap.GetPixel(bitmap.Width / 2, bitmap.Height / 2);

        // Untouched, the BT.2020 red 0x60 would come out as ~0x60; mapped into BT.709 it is far lower.
        Assert.True(Math.Abs(centre.Red - 0x60) > 15, $"red {centre.Red} was not tone-mapped");
    }

    public void Dispose()
    {
        Directory.Delete(dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static string VideoFilter(string? toneMap)
    {
        var args = CaptureVideoFrameExtractor.FfmpegArguments("/data/v.mov", 3, "/tmp/c_%05d.jpg", 9, toneMap).ToList();
        return args[args.IndexOf("-vf") + 1];
    }

    private static bool FfmpegAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, UseShellExecute = false });
            p!.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task RunFfmpegAsync(params string[] args)
    {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        args.ToList().ForEach(info.ArgumentList.Add);
        using var p = Process.Start(info)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, err);
    }
}
