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
public sealed record CaptureVideoProbe(double DurationSeconds, int Width, int Height)
{
    /// <summary>ffprobe's <c>color_transfer</c> (e.g. <c>arib-std-b67</c> for iPhone HLG); null when untagged.</summary>
    public string? ColorTransfer { get; init; }

    /// <summary>ffprobe's <c>color_primaries</c> (e.g. <c>bt2020</c>); null when untagged.</summary>
    public string? ColorPrimaries { get; init; }

    /// <summary>ffprobe's <c>color_space</c> (the YCbCr matrix, e.g. <c>bt2020nc</c>); null when untagged.</summary>
    public string? ColorSpace { get; init; }
}

/// <summary>How many frames to take: about <paramref name="FramesPerSecond"/>, never more than <paramref name="MaxFrames"/>.</summary>
public sealed record CaptureVideoFrameRequest(double FramesPerSecond, int MaxFrames, TimeSpan Timeout)
{
    /// <summary>Candidates per kept frame (the sharpest of each run of this many wins).</summary>
    public int Window { get; init; } = CaptureVideoFrameExtractor.Window;

    /// <summary>ffmpeg's <c>-q:v</c> for the candidate JPEGs (2 best … 31 worst).</summary>
    public int JpegQ { get; init; } = CaptureVideoFrameExtractor.JpegQ;

    /// <summary>Long edge each candidate's sharpness is scored at.</summary>
    public int ScoreEdge { get; init; } = CaptureFrameSharpness.ScoreEdge;
}
