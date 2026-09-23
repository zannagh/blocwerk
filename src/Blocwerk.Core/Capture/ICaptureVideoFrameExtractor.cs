// <copyright file="ICaptureVideoFrameExtractor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Capture;

/// <summary>
/// Turns a capture's walk-along video into still frames for the photo-real (splat) stage only:
/// rotation applied, ≤ <see cref="CaptureVideoFrameExtractor.MaxEdge"/> px on the long edge, the
/// sharpest frame per window, capped in number, JPEG with no metadata at all.
/// </summary>
public interface ICaptureVideoFrameExtractor
{
    /// <summary>Reads the duration and displayed size. Throws <see cref="InvalidDataException"/> for anything that is not a readable video.</summary>
    Task<CaptureVideoProbe> ProbeAsync(string videoPath, CancellationToken ct);

    /// <summary>
    /// Extracts the frames, in video order, as metadata-free JPEG bytes. <paramref name="progress"/>
    /// gets 0..1. Throws <see cref="InvalidDataException"/> when ffmpeg cannot read the video.
    /// </summary>
    Task<IReadOnlyList<byte[]>> ExtractAsync(
        string videoPath, CaptureVideoFrameRequest request, IProgress<double>? progress, CancellationToken ct);
}

/// <summary>What ffprobe says about an uploaded video.</summary>
public sealed record CaptureVideoProbe(double DurationSeconds, int Width, int Height);

/// <summary>How many frames to take: about <paramref name="FramesPerSecond"/>, never more than <paramref name="MaxFrames"/>.</summary>
public sealed record CaptureVideoFrameRequest(double FramesPerSecond, int MaxFrames, TimeSpan Timeout);
