// <copyright file="CaptureVideoFrameExtractor.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json;
using Blocwerk.Core.Configuration;

namespace Blocwerk.Core.Capture;

/// <summary>
/// <see cref="ICaptureVideoFrameExtractor"/> over ffmpeg/ffprobe (the binaries the beta videos use).
/// ffmpeg decodes at <see cref="Window"/>× the target rate with its default auto-rotation (the
/// display matrix is applied to the pixels), scales to ≤ <see cref="MaxEdge"/> px, drops every
/// container tag (<c>-map_metadata -1</c>: GPS "location", make, model) and writes candidate JPEGs to
/// a private temp folder; the sharpest candidate of each window is kept and re-stripped with
/// <see cref="ImageMetadataStripper"/>, so not even ffmpeg's own comment segment leaves the server.
/// HDR clips (iPhone HLG / Dolby Vision, PQ) are tone-mapped to SDR BT.709 (<see cref="HdrToneMapFilter"/>).
/// </summary>
public sealed class CaptureVideoFrameExtractor(BlocwerkSettings settings) : ICaptureVideoFrameExtractor
{
    public const int MaxEdge = 1920;

    /// <summary>Candidates per kept frame: the sharpest of each run of this many wins.</summary>
    public const int Window = 3;

    /// <summary>
    /// Largest picture ffmpeg/ffprobe will decode (8192², above any phone's 8K). Passed as the
    /// decoder's <c>-max_pixels</c>, so a MOV carrying a 65535² MJPEG/PNG frame — or switching to one
    /// mid-stream — fails to decode instead of allocating gigabytes inside the app container.
    /// </summary>
    public const long MaxPixels = 8192L * 8192;

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(1);

    public async Task<CaptureVideoProbe> ProbeAsync(string videoPath, CancellationToken ct)
    {
        string[] args =
        [
            "-v", "error", "-max_pixels", MaxPixelsArgument, "-select_streams", "v:0",
            "-show_entries", "format=duration:stream=width,height,duration,color_transfer,color_primaries,color_space"
                + ":stream_side_data=rotation:stream_tags=rotate",
            "-of", "json", videoPath,
        ];
        var json = await CaptureToolProcess.RunAsync(settings.BetaVideo.FfprobePath, args, ProbeTimeout, null, ct);
        return ParseProbe(json) ?? throw new InvalidDataException("The file is not a readable video.");
    }

    /// <summary>
    /// ffprobe's JSON → duration, DISPLAYED size (a ±90° rotation swaps them) and colour tags; null
    /// without a video stream.
    /// </summary>
    public static CaptureVideoProbe? ParseProbe(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("streams", out var streams) || streams.GetArrayLength() == 0)
        {
            return null;
        }

        var stream = streams[0];
        var width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
        var height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;
        var duration = Seconds(root, "format") ?? Seconds(stream, null) ?? 0;
        if (width <= 0 || height <= 0 || duration <= 0 || (long)width * height > MaxPixels)
        {
            return null;
        }

        var quarterTurn = Math.Abs(Rotation(stream)) % 180 == 90;
        return new CaptureVideoProbe(duration, quarterTurn ? height : width, quarterTurn ? width : height)
        {
            ColorTransfer = Text(stream, "color_transfer"),
            ColorPrimaries = Text(stream, "color_primaries"),
            ColorSpace = Text(stream, "color_space"),
        };
    }

    public async Task<IReadOnlyList<byte[]>> ExtractAsync(
        string videoPath, CaptureVideoFrameRequest request, IProgress<double>? progress, CancellationToken ct)
    {
        var probe = await ProbeAsync(videoPath, ct);
        var target = Math.Min(request.FramesPerSecond, Math.Max(1, request.MaxFrames) / probe.DurationSeconds);
        var toneMap = HdrToneMapFilter.IsHdr(probe.ColorTransfer)
            ? HdrToneMapFilter.Select(probe.ColorTransfer, await FfmpegFilterCatalog.GetAsync(settings.BetaVideo.FfmpegPath, ct))
            : null;
        var work = Directory.CreateTempSubdirectory("blocwerk-capture-video-");
        try
        {
            var args = FfmpegArguments(
                videoPath, target * Window, Path.Combine(work.FullName, "c_%05d.jpg"), MaxCandidates(request.MaxFrames), toneMap);
            await CaptureToolProcess.RunAsync(
                settings.BetaVideo.FfmpegPath, args, request.Timeout,
                line => ReportDecode(line, probe.DurationSeconds, progress), ct);
            var candidates = work.EnumerateFiles("c_*.jpg").OrderBy(f => f.Name, StringComparer.Ordinal).ToList();
            var scores = new List<double>(candidates.Count);
            for (var i = 0; i < candidates.Count; i++)
            {
                scores.Add(CaptureFrameSharpness.Score(await File.ReadAllBytesAsync(candidates[i].FullName, ct)));
                progress?.Report(0.8 + (0.2 * (i + 1) / candidates.Count));
            }

            var frames = new List<byte[]>();
            foreach (var index in CaptureFrameSharpness.Select(scores, Window, request.MaxFrames))
            {
                frames.Add(ImageMetadataStripper.Strip(await File.ReadAllBytesAsync(candidates[index].FullName, ct)));
            }

            return frames;
        }
        finally
        {
            work.Delete(recursive: true);
        }
    }

    /// <summary>
    /// Most candidate JPEGs one extraction may write: the rate is planned from the container's declared
    /// duration, which a crafted file can understate while its real timestamps run for hours (the fps
    /// filter then duplicates frames without end). Twice the plan, plus a window of slack.
    /// </summary>
    public static int MaxCandidates(int maxFrames) => (2 * Math.Max(1, maxFrames) * Window) + Window;

    /// <summary>
    /// The ffmpeg call: first video stream only, no tags, auto-rotated, ≤ MaxEdge, progress on stdout;
    /// decoding capped at <see cref="MaxPixels"/> per picture, <see cref="CaptureVideoFiles.MaxDuration"/>
    /// of input and <paramref name="maxCandidates"/> written frames. <paramref name="toneMap"/> (from
    /// <see cref="HdrToneMapFilter.Select"/>) runs after the downscale, so HDR is mapped on the small frames only.
    /// </summary>
    public static IReadOnlyList<string> FfmpegArguments(
        string videoPath, double candidateFps, string outputPattern, int maxCandidates, string? toneMap = null) =>
    [
        "-nostdin", "-hide_banner", "-v", "error",
        "-max_pixels", MaxPixelsArgument,
        "-t", CaptureVideoFiles.MaxDuration.TotalSeconds.ToString("0", CultureInfo.InvariantCulture),
        "-i", videoPath,
        "-map", "0:v:0", "-an", "-sn", "-dn", "-map_metadata", "-1",
        "-vf", string.Create(
            CultureInfo.InvariantCulture,
            $"fps={candidateFps:0.####},scale=w='min({MaxEdge},iw)':h='min({MaxEdge},ih)':force_original_aspect_ratio=decrease")
            + (string.IsNullOrEmpty(toneMap) ? string.Empty : "," + toneMap),
        "-frames:v", maxCandidates.ToString(CultureInfo.InvariantCulture),
        "-q:v", "3", "-progress", "pipe:1", "-nostats",
        "-f", "image2", outputPattern,
    ];

    private static string MaxPixelsArgument => MaxPixels.ToString(CultureInfo.InvariantCulture);

    private static void ReportDecode(string line, double duration, IProgress<double>? progress)
    {
        const string key = "out_time_us=";
        if (progress is not null && line.StartsWith(key, StringComparison.Ordinal)
            && long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
        {
            progress.Report(0.8 * Math.Clamp(us / 1e6 / duration, 0, 1));
        }
    }

    private static double? Seconds(JsonElement element, string? child)
    {
        var holder = element;
        if (child is not null && !element.TryGetProperty(child, out holder))
        {
            return null;
        }

        if (!holder.TryGetProperty("duration", out var d))
        {
            return null;
        }

        var seconds = d.ValueKind == JsonValueKind.Number ? d.GetDouble()
            : double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) ? s : 0;
        return seconds > 0 && double.IsFinite(seconds) ? seconds : null;
    }

    private static string? Text(JsonElement stream, string name) =>
        stream.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: > 0 } text && text != "unknown"
            ? text
            : null;

    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var sides))
        {
            foreach (var side in sides.EnumerateArray())
            {
                if (side.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Number)
                {
                    return (int)Math.Round(r.GetDouble());
                }
            }
        }

        return stream.TryGetProperty("tags", out var tags) && tags.TryGetProperty("rotate", out var rotate)
               && int.TryParse(rotate.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var deg)
            ? deg
            : 0;
    }
}
