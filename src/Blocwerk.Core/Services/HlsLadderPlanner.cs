using System.Globalization;
using System.Text;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;

namespace Blocwerk.Core.Services;

/// <summary>
/// Pure planning for the HLS ladder: which rungs to emit for a given source, and the exact ffmpeg
/// argument string that produces master.m3u8 + per-rung variant playlists and segments. Kept free of
/// ffmpeg and the filesystem so the "cap to source height, never upscale" rule and the command shape
/// are unit-testable without a transcoder on the box.
/// </summary>
public static class HlsLadderPlanner
{
    /// <summary>Master playlist file name the muxer writes and clients load first.</summary>
    public const string MasterPlaylistName = "master.m3u8";

    /// <summary>
    /// Sentinel <see cref="Abstractions.VideoProbeResult.RotationDegrees"/> for a clip that carries a
    /// display rotation we do NOT confidently handle (anything other than a clean 0/90/180/270, or a
    /// rotation present but unparseable). The ladder is skipped for such a clip so it falls back to the
    /// auto-rotated MP4 rather than risk a sideways HLS rendition. See <see cref="IsSupportedRotation"/>.
    /// </summary>
    public const int UnhandledRotation = -1;

    /// <summary>
    /// Whether <paramref name="rotationDegrees"/> is one this planner can rotate upright (0/90/180/270).
    /// Anything else — including <see cref="UnhandledRotation"/> — means the caller must skip HLS.
    /// </summary>
    public static bool IsSupportedRotation(int rotationDegrees) =>
        rotationDegrees is 0 or 90 or 180 or 270;

    /// <summary>
    /// The DISPLAYED height for rung selection. A 90/270 rotation swaps the axes, so the displayed
    /// height is the source's coded WIDTH (a 1920×1080 clip shot in portrait displays as 1080×1920,
    /// height 1920); 0/180 keep the coded height.
    /// </summary>
    public static int DisplayedHeight(int codedWidth, int codedHeight, int rotationDegrees) =>
        rotationDegrees is 90 or 270 ? codedWidth : codedHeight;

    /// <summary>
    /// The DISPLAYED width, the companion to <see cref="DisplayedHeight"/>. A 90/270 rotation swaps the
    /// axes, so the displayed width is the source's coded HEIGHT (a 1920×1080 clip shot in portrait
    /// displays 1080 wide); 0/180 keep the coded width.
    /// </summary>
    public static int DisplayedWidth(int codedWidth, int codedHeight, int rotationDegrees) =>
        rotationDegrees is 90 or 270 ? codedHeight : codedWidth;

    /// <summary>
    /// The ffmpeg filter that rotates a decoded frame upright, replicating what <c>-autorotate</c> does
    /// on the MP4 path (which rotates by the negative of the display-matrix angle). Empty for 0. The
    /// angle is in the upright <c>rotate</c>-tag convention: 90 → clockwise, 270 → counter-clockwise.
    /// </summary>
    public static string RotationFilter(int rotationDegrees) => rotationDegrees switch
    {
        90 => "transpose=1",
        270 => "transpose=2",
        180 => "transpose=1,transpose=1",
        _ => string.Empty,
    };

    /// <summary>
    /// The rungs to encode for a source of <paramref name="sourceHeight"/> px: every ladder rung whose
    /// height is at or below the source (so nothing is upscaled), and always at least the smallest rung
    /// even when the source is tinier than the whole ladder. Result is ascending by height. An unknown
    /// (0 or negative) source height keeps the smallest rung only, which is safe for any input.
    /// </summary>
    public static IReadOnlyList<HlsRung> SelectRungs(IReadOnlyList<HlsRung> ladder, int sourceHeight)
    {
        var ordered = ladder.OrderBy(r => r.Height).ToList();
        if (ordered.Count == 0)
        {
            return ordered;
        }

        var selected = sourceHeight > 0
            ? ordered.Where(r => r.Height <= sourceHeight).ToList()
            : [];

        if (selected.Count == 0)
        {
            selected.Add(ordered[0]);
        }

        return selected;
    }

    /// <summary>
    /// The full ffmpeg argument string for the ladder. One invocation: a <c>-filter_complex</c> that
    /// splits the decoded video and scales each branch to its rung height, per-rung H.264/AAC output
    /// options with keyframes pinned to the segment length, and a <c>-var_stream_map</c> that groups
    /// each video (and, when present, audio) into its own variant. Paths are quoted; the segment and
    /// playlist templates use ffmpeg's <c>%v</c> variant index.
    /// </summary>
    public static string BuildArguments(
        string inputPath,
        string outputDirectory,
        IReadOnlyList<HlsRung> rungs,
        int segmentSeconds,
        bool hasAudio,
        int rotationDegrees = 0)
    {
        var segments = segmentSeconds > 0 ? segmentSeconds : 4;

        // Keyframe interval: assume 30 fps (no reliable source fps here) so a keyframe lands at least
        // every segment. sc_threshold 0 stops the encoder inserting scene-cut keyframes that would
        // otherwise drift segment boundaries between rungs.
        var gop = (segments * 30).ToString(CultureInfo.InvariantCulture);
        var sep = Path.DirectorySeparatorChar;

        var sb = new StringBuilder();
        sb.Append("-y -hide_banner -loglevel error ");

        // Disable ffmpeg's implicit auto-rotation of the decoded stream. Modern ffmpeg (6.x) DOES
        // auto-rotate a [0:v] stream feeding a complex graph, so without this the display matrix would
        // rotate the frame AND our explicit transpose below would rotate it again → a sideways ladder.
        // With -noautorotate the frame reaches the graph in its coded orientation and the transpose is
        // the single, deterministic rotation, matching the auto-rotated MP4 exactly (verified by PSNR).
        sb.Append("-noautorotate ");
        sb.Append("-i ").Append(Quote(inputPath)).Append(' ');
        sb.Append("-filter_complex ").Append(Quote(BuildFilter(rungs, rotationDegrees))).Append(' ');

        for (var i = 0; i < rungs.Count; i++)
        {
            var rung = rungs[i];
            var idx = i.ToString(CultureInfo.InvariantCulture);
            sb.Append("-map ").Append(Quote($"[v{i}out]")).Append(' ');
            if (hasAudio)
            {
                sb.Append("-map a:0 ");
            }

            sb.Append($"-c:v:{idx} libx264 -preset veryfast -profile:v high -pix_fmt yuv420p ");
            sb.Append($"-b:v:{idx} {rung.VideoKbps}k -maxrate:v:{idx} {rung.VideoKbps * 3 / 2}k -bufsize:v:{idx} {rung.VideoKbps * 2}k ");
            sb.Append($"-g:v:{idx} {gop} -keyint_min:v:{idx} {gop} -sc_threshold:v:{idx} 0 ");
            if (hasAudio)
            {
                sb.Append($"-c:a:{idx} aac -b:a:{idx} {rung.AudioKbps}k -ac 2 ");
            }
        }

        sb.Append("-var_stream_map ").Append(Quote(BuildStreamMap(rungs.Count, hasAudio))).Append(' ');
        sb.Append("-master_pl_name ").Append(MasterPlaylistName).Append(' ');
        sb.Append("-f hls -hls_time ").Append(segments.ToString(CultureInfo.InvariantCulture)).Append(' ');
        sb.Append("-hls_playlist_type vod -hls_flags independent_segments -hls_segment_type mpegts ");
        sb.Append("-hls_segment_filename ").Append(Quote($"{outputDirectory}{sep}v%v_%03d.ts")).Append(' ');
        sb.Append(Quote($"{outputDirectory}{sep}v%v.m3u8"));

        return sb.ToString();
    }

    private static string BuildFilter(IReadOnlyList<HlsRung> rungs, int rotationDegrees)
    {
        var sb = new StringBuilder();

        // Rotate ONCE up front (cheaper and consistent across rungs), then split. The invocation passes
        // -noautorotate, so [0:v] arrives in its CODED orientation and this transpose is the only
        // rotation applied — it puts the frame into its displayed orientation, matching the auto-rotated
        // MP4. After rotation the frame is already displayed-oriented, so the scale below targets the
        // displayed height directly.
        sb.Append("[0:v]");
        var rotation = RotationFilter(rotationDegrees);
        if (rotation.Length > 0)
        {
            sb.Append(rotation).Append(',');
        }

        sb.Append("split=").Append(rungs.Count.ToString(CultureInfo.InvariantCulture));
        for (var i = 0; i < rungs.Count; i++)
        {
            sb.Append($"[v{i}]");
        }

        for (var i = 0; i < rungs.Count; i++)
        {
            // -2 keeps the aspect ratio and forces an even width; the rungs are already capped to the
            // displayed height, so this only ever scales down.
            sb.Append($";[v{i}]scale=-2:{rungs[i].Height}[v{i}out]");
        }

        return sb.ToString();
    }

    private static string BuildStreamMap(int count, bool hasAudio)
    {
        var parts = new string[count];
        for (var i = 0; i < count; i++)
        {
            parts[i] = hasAudio ? $"v:{i},a:{i}" : $"v:{i}";
        }

        return string.Join(' ', parts);
    }

    private static string Quote(string value) => $"\"{value}\"";
}
