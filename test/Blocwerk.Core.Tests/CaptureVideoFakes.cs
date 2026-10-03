// <copyright file="CaptureVideoFakes.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Capture;

namespace Blocwerk.Core.Tests;

/// <summary>
/// A scripted <see cref="ICaptureVideoFrameExtractor"/>: every file "is" a 42 s video, and extraction
/// yields <see cref="FrameCount"/> distinct tiny JPEGs (or throws <see cref="Failure"/>).
/// </summary>
internal sealed class FakeVideoFrameExtractor : ICaptureVideoFrameExtractor
{
    public int FrameCount { get; set; } = 5;

    public InvalidDataException? Failure { get; set; }

    public List<string> Probed { get; } = [];

    /// <summary>Runs inside every extraction, before <see cref="Failure"/> is thrown (lets a test look at state mid-run).</summary>
    public Action? DuringExtract { get; set; }

    public List<(string Path, CaptureVideoFrameRequest Request)> Extracted { get; } = [];

    public Task<CaptureVideoProbe> ProbeAsync(string videoPath, CancellationToken ct)
    {
        Probed.Add(videoPath);
        return Task.FromResult(new CaptureVideoProbe(42, 1920, 1080));
    }

    /// <summary>When set, extraction fails with <see cref="Failure"/> after storing this many frames.</summary>
    public int? FailAfter { get; set; }

    public async Task<int> ExtractAsync(
        string videoPath,
        CaptureVideoFrameRequest request,
        Func<byte[], CancellationToken, Task> store,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        Extracted.Add((videoPath, request));
        DuringExtract?.Invoke();
        if (Failure is not null && FailAfter is null)
        {
            throw Failure;
        }

        for (var i = 0; i < FrameCount; i++)
        {
            if (Failure is not null && i == FailAfter)
            {
                throw Failure;
            }

            await store(CaptureScenario.TinyJpeg(seed: 100 + i), ct);
        }

        progress?.Report(1);
        return FrameCount;
    }

    /// <summary>A frame store that keeps the frames in <paramref name="frames"/> (for tests of the real extractor).</summary>
    public static Func<byte[], CancellationToken, Task> Collect(List<byte[]> frames) => (frame, _) =>
    {
        frames.Add(frame);
        return Task.CompletedTask;
    };

    /// <summary>Bytes that pass the upload's container sniff: an ISO-media <c>ftyp</c> box, then filler.</summary>
    public static MemoryStream Mp4Stream(int size = 4096)
    {
        var bytes = new byte[size];
        "\0\0\0\u0018ftypisom"u8.ToArray().CopyTo(bytes, 0);
        return new MemoryStream(bytes);
    }
}
