// <copyright file="HlsLadderFfmpegTests.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Diagnostics;
using System.Globalization;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>
/// The HLS ladder against real ffmpeg (skipped where it is not installed): a synthetic clip at or below
/// the smallest rung comes out as one valid variant at its own displayed size — never upscaled to 360p —
/// and ffprobe reads the segment back with those dimensions.
/// </summary>
public sealed class HlsLadderFfmpegTests : IDisposable
{
    private readonly string dir = Directory.CreateTempSubdirectory("blocwerk-hls-test-").FullName;

    public void Dispose()
    {
        Directory.Delete(dir, recursive: true);
    }

    [SkippableTheory]
    [InlineData(640, 360, 0, 640, 360)]
    [InlineData(320, 180, 0, 320, 180)]
    [InlineData(320, 180, 90, 180, 320)]
    public async Task SmallClip_BecomesOneRungAtItsDisplayedSize(int width, int height, int rotation, int outWidth, int outHeight)
    {
        Skip.IfNot(ToolAvailable("ffmpeg") && ToolAvailable("ffprobe"), "ffmpeg/ffprobe are not installed");
        var clip = await MakeClipAsync(width, height, rotation);
        var transcoder = new FfmpegVideoTranscoder(new BlocwerkSettings(), NullLogger<FfmpegVideoTranscoder>.Instance);
        var probe = await transcoder.ProbeAsync(clip, default);
        var output = Path.Combine(dir, "hls");

        await transcoder.TranscodeHlsAsync(clip, output, probe, default);

        var master = await File.ReadAllTextAsync(Path.Combine(output, HlsLadderPlanner.MasterPlaylistName));
        Assert.Single(master.Split('\n'), l => l.StartsWith("#EXT-X-STREAM-INF", StringComparison.Ordinal));
        Assert.Contains($"RESOLUTION={outWidth}x{outHeight}", master);
        var size = await RunAsync("ffprobe", "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=width,height",
            "-of", "csv=p=0", Path.Combine(output, "v0_000.ts"));
        var sizes = size.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct();
        Assert.Equal($"{outWidth},{outHeight}", Assert.Single(sizes));
    }

    private static bool ToolAvailable(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, "-version") { RedirectStandardOutput = true, UseShellExecute = false });
            p!.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>testsrc2 with a tone, H.264, then a stream copy that adds the display rotation.</summary>
    private async Task<string> MakeClipAsync(int width, int height, int rotation)
    {
        var plain = Path.Combine(dir, "plain.mp4");
        var tagged = Path.Combine(dir, "tagged.mp4");
        await RunAsync("ffmpeg", "-y", "-v", "error", "-f", "lavfi", "-i", $"testsrc2=size={width}x{height}:rate=30:duration=3",
            "-f", "lavfi", "-i", "sine=frequency=440:duration=3", "-c:v", "libx264", "-pix_fmt", "yuv420p", "-c:a", "aac", "-shortest", plain);
        await RunAsync("ffmpeg", "-y", "-v", "error", "-display_rotation", rotation.ToString(CultureInfo.InvariantCulture),
            "-i", plain, "-c", "copy", tagged);
        return tagged;
    }

    private static async Task<string> RunAsync(string tool, params string[] args)
    {
        var info = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        args.ToList().ForEach(info.ArgumentList.Add);
        using var p = Process.Start(info)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.True(p.ExitCode == 0, err);
        return await stdout;
    }
}
