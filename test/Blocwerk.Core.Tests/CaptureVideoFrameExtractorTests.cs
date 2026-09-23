// <copyright file="CaptureVideoFrameExtractorTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Text;
using Blocwerk.Core.Capture;
using Blocwerk.Core.Configuration;
using SkiaSharp;

namespace Blocwerk.Core.Tests;

/// <summary>
/// Frame extraction against real ffmpeg (skipped where it is not installed): a synthetic clip with a
/// 90° display rotation and a GPS "location" tag must come out upright, ≤ 1920 px, capped in number
/// and with no metadata at all. Plus the pure sharpness selection and probe parsing.
/// </summary>
public class CaptureVideoFrameExtractorTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("blocwerk-video-test-").FullName;

    [SkippableFact]
    public async Task SyntheticRotatedClipWithGps_GivesUprightCappedMetadataFreeFrames()
    {
        Skip.IfNot(FfmpegAvailable(), "ffmpeg is not installed");
        var clip = await MakeClipAsync(width: 2400, height: 1200, seconds: 8, rotation: 90);
        var extractor = new CaptureVideoFrameExtractor(new BlocwerkSettings());

        var probe = await extractor.ProbeAsync(clip, CancellationToken.None);
        var frames = await extractor.ExtractAsync(clip, new CaptureVideoFrameRequest(2.5, 7, TimeSpan.FromMinutes(2)), null, CancellationToken.None);

        Assert.Equal((1200, 2400), (probe.Width, probe.Height)); // displayed size: rotated
        Assert.InRange(frames.Count, 5, 7);                        // 8 s at 2.5 fps = 20, capped to 7
        foreach (var frame in frames)
        {
            using var bitmap = SKBitmap.Decode(frame);
            Assert.Equal((960, 1920), (bitmap.Width, bitmap.Height)); // portrait, long edge 1920
            var text = Encoding.Latin1.GetString(frame);
            Assert.DoesNotContain("Exif", text, StringComparison.Ordinal);
            Assert.DoesNotContain("48.1351", text, StringComparison.Ordinal);
            Assert.DoesNotContain("TestPhone", text, StringComparison.Ordinal);
            Assert.DoesNotContain("Lavc", text, StringComparison.Ordinal);
            Assert.False(HasSegment(frame, 0xE1), "no APP1 (EXIF/XMP) segment");
        }
    }

    [SkippableFact]
    public async Task ANonVideo_IsRefusedByTheProbe()
    {
        Skip.IfNot(FfmpegAvailable(), "ffmpeg is not installed");
        var path = Path.Combine(dir, "fake.mp4");
        await File.WriteAllBytesAsync(path, "\0\0\0\u0018ftypisom not really a video"u8.ToArray());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => new CaptureVideoFrameExtractor(new BlocwerkSettings()).ProbeAsync(path, CancellationToken.None));
    }

    [Fact]
    public void Select_KeepsTheSharpestPerWindow_ThenThinsEvenlyToTheCap()
    {
        double[] scores = [1, 9, 2, 5, 4, 3, 0, 0, 7, 8];
        Assert.Equal([1, 3, 8, 9], CaptureFrameSharpness.Select(scores, window: 3, max: 10));
        Assert.Equal([1, 9], CaptureFrameSharpness.Select(scores, window: 3, max: 2));
    }

    [Fact]
    public void LaplacianVariance_IsZeroForFlat_AndHighForEdges()
    {
        var flat = Enumerable.Repeat((byte)128, 16 * 16).ToArray();
        var checker = Enumerable.Range(0, 16 * 16).Select(i => (byte)(((i % 16) + (i / 16)) % 2 == 0 ? 0 : 255)).ToArray();
        Assert.Equal(0, CaptureFrameSharpness.LaplacianVariance(flat, 16, 16, 16));
        Assert.True(CaptureFrameSharpness.LaplacianVariance(checker, 16, 16, 16) > 1000);
    }

    [Fact]
    public void ParseProbe_SwapsTheSizeForAQuarterTurn()
    {
        const string json = """
            {"streams":[{"width":1920,"height":1080,"side_data_list":[{"rotation":-90}]}],"format":{"duration":"61.5"}}
            """;
        Assert.Equal(new CaptureVideoProbe(61.5, 1080, 1920), CaptureVideoFrameExtractor.ParseProbe(json));
        Assert.Null(CaptureVideoFrameExtractor.ParseProbe("""{"streams":[],"format":{"duration":"3"}}"""));
    }

    public void Dispose()
    {
        Directory.Delete(dir, recursive: true);
        GC.SuppressFinalize(this);
    }

    private static bool HasSegment(byte[] jpeg, byte marker)
    {
        // Walk the header segments up to the first scan.
        var i = 2;
        while (i + 4 <= jpeg.Length && jpeg[i] == 0xFF && jpeg[i + 1] != 0xDA)
        {
            if (jpeg[i + 1] == marker)
            {
                return true;
            }

            i += 2 + ((jpeg[i + 2] << 8) | jpeg[i + 3]);
        }

        return false;
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

    /// <summary>testsrc2 → MPEG-4 part 2 (always built in), then a stream copy that adds the display
    /// rotation and phone-style tags (a GPS "location", a make).</summary>
    private async Task<string> MakeClipAsync(int width, int height, int seconds, int rotation)
    {
        var plain = Path.Combine(dir, "plain.mp4");
        var tagged = Path.Combine(dir, "tagged.mp4");
        await RunAsync("-y", "-v", "error", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=10:duration={seconds}",
            "-c:v", "mpeg4", "-q:v", "8", plain);
        await RunAsync("-y", "-v", "error", "-display_rotation", rotation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-i", plain, "-c", "copy", "-metadata", "location=+48.1351+011.5820/", "-metadata", "make=TestPhone", tagged);
        return tagged;
    }

    private static async Task RunAsync(params string[] args)
    {
        var info = new ProcessStartInfo("ffmpeg") { RedirectStandardError = true, UseShellExecute = false };
        args.ToList().ForEach(info.ArgumentList.Add);
        using var p = Process.Start(info)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, err);
    }
}
