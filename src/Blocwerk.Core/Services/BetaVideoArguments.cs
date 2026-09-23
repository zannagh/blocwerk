// <copyright file="BetaVideoArguments.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.Services;

/// <summary>
/// The ffmpeg argument strings for the beta-clip MP4 outputs, pure so their shape — above all the
/// privacy scrub — is testable without ffmpeg on the box.
/// </summary>
public static class BetaVideoArguments
{
    /// <summary>
    /// Output options that strip everything identifying from a clip: the container metadata (phones put
    /// the recording location there — <c>location</c>, <c>com.apple.quicktime.location.ISO6709</c>, which
    /// the MP4 muxer would otherwise re-emit as a binary <c>loci</c> atom), the per-stream tags (device
    /// handler names, encoder), chapters, and any data/subtitle streams (Apple's timed-metadata tracks).
    /// The display matrix is stream side data, not a tag, so a remuxed portrait clip keeps its rotation.
    /// </summary>
    public const string MetadataScrub =
        "-map_metadata -1 -map_metadata:s:v -1 -map_metadata:s:a -1 -map_chapters -1 -dn -sn";

    /// <summary>Stream-copy into a faststart MP4 with the metadata scrubbed.</summary>
    public static string Remux(string inputPath, string outputPath) => string.Join(' ',
        "-y", "-hide_banner", "-loglevel", "error",
        "-i", Quote(inputPath),
        "-c", "copy", MetadataScrub, "-movflags", "+faststart",
        Quote(outputPath));

    /// <summary>
    /// Scale filter that only ever shrinks: the bounding box is the cap clamped to the frame's own size
    /// (<c>min(1280,iw)</c> × <c>min(1280,ih)</c>), so a clip already inside the cap keeps its size, and
    /// <c>force_original_aspect_ratio=decrease</c> keeps the aspect ratio while
    /// <c>force_divisible_by=2</c> rounds both edges DOWN to even (yuv420p needs that). It runs after
    /// ffmpeg's autorotate, so <c>iw</c>/<c>ih</c> are the displayed (upright) size and a portrait clip
    /// stays portrait. The single quotes keep the commas inside <c>min()</c> from splitting the graph.
    /// A plain 1280×1280 box used to upscale small clips (640×360 rotated came out 720×1280).
    /// </summary>
    public const string DownscaleOnlyFilter =
        "scale=w='min(1280,iw)':h='min(1280,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2";

    /// <summary>
    /// Single-pass ABR with a capped max rate; H.264 High/4.1 in yuv420p + AAC-LC stereo, scaled DOWN
    /// to fit 720p (long edge 1280) and never up (<see cref="DownscaleOnlyFilter"/>).
    /// ffmpeg auto-rotates by the display matrix, so portrait phone clips come out upright without any
    /// rotation metadata the browser would have to honour.
    /// </summary>
    public static string Transcode(string inputPath, string outputPath, long kbps) => string.Join(' ',
        "-y", "-hide_banner", "-loglevel", "error",
        "-i", Quote(inputPath),
        "-c:v", "libx264", "-preset", "veryfast", "-profile:v", "high", "-level", "4.1",
        "-pix_fmt", "yuv420p",
        "-b:v", $"{kbps}k", "-maxrate", $"{kbps * 3 / 2}k", "-bufsize", $"{kbps * 2}k",
        "-vf", Quote(DownscaleOnlyFilter),
        "-c:a", "aac", "-b:a", "128k", "-ac", "2",
        MetadataScrub,
        "-movflags", "+faststart",
        Quote(outputPath));

    private static string Quote(string value) => $"\"{value}\"";
}
