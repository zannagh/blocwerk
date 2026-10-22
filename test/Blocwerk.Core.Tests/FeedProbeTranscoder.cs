// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Tests;

/// <summary>A stand-in transcoder for the feed's normalizer tests: writes fixed bytes, or fails the encode when asked.</summary>
internal sealed class FeedProbeTranscoder : IVideoTranscoder
{
    private static readonly byte[] Encoded = [0x01, 0x02, 0x03, 0x04];

    public bool ThrowOnEncode { get; init; }

    public Task<VideoProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken) =>
        Task.FromResult(new VideoProbeResult(5, "hevc", "aac", "yuv420p", true));

    public Task<VideoTranscodeResult> RemuxAsync(string inputPath, string outputPath, CancellationToken cancellationToken) =>
        WriteAsync(outputPath, cancellationToken);

    public Task<VideoTranscodeResult> TranscodeAsync(string inputPath, string outputPath, CancellationToken cancellationToken) =>
        ThrowOnEncode ? throw new InvalidOperationException("ffmpeg failed (fake).") : WriteAsync(outputPath, cancellationToken);

    public async Task TranscodeHlsAsync(string inputPath, string outputDirectory, VideoProbeResult probe, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(outputDirectory);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "master.m3u8"), "#EXTM3U\nv0.m3u8\n", cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "v0.m3u8"), "#EXTM3U\nv0_000.ts\n", cancellationToken);
        await File.WriteAllBytesAsync(Path.Combine(outputDirectory, "v0_000.ts"), Encoded, cancellationToken);
    }

    public Task<byte[]?> ExtractPosterAsync(string normalizedMp4Path, double durationSeconds, CancellationToken cancellationToken) =>
        Task.FromResult<byte[]?>([0x0A, 0x0B]);

    private static async Task<VideoTranscodeResult> WriteAsync(string outputPath, CancellationToken ct)
    {
        await File.WriteAllBytesAsync(outputPath, Encoded, ct);
        return new VideoTranscodeResult(Encoded.Length, "video/mp4");
    }
}
