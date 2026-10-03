// <copyright file="HlsRung.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Configuration;

/// <summary>
/// One rung of the HLS adaptive-bitrate ladder: a target output <paramref name="Height"/> (px) with
/// its video and audio bitrates (kbps). The transcoder scales the source down to this height (never
/// up) and emits one variant playlist + segment set per included rung.
/// </summary>
public record HlsRung(int Height, int VideoKbps, int AudioKbps)
{
    /// <summary>
    /// An exact output width (even, px), or null to let ffmpeg derive it from the aspect ratio. Only
    /// the source-sized rung the planner makes for a clip smaller than the whole ladder sets it, so
    /// that rung can never come out even one pixel wider than the source.
    /// </summary>
    public int? Width { get; init; }
}
