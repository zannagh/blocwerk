namespace Blocwerk.Core.Abstractions;

/// <summary>The outcome of a transcode/remux: the new file's size and MIME type.</summary>
public record VideoTranscodeResult(long SizeBytes, string ContentType);

/// <summary>
/// What ffprobe reports about a clip — enough to decide whether it is already web-safe (a cheap
/// remux will do) or must be fully re-encoded. Codec/pixel-format strings are ffmpeg's own names,
/// lower-cased; null when the clip has no such stream.
/// </summary>
public record VideoProbeResult(
    double DurationSeconds,
    string? VideoCodec,
    string? AudioCodec,
    string? PixelFormat,
    bool HasAudio);

/// <summary>
/// Normalizes beta clips to a universally playable rendition. Implemented with ffmpeg/ffprobe, which
/// the runtime image ships (see docker/Dockerfile) precisely for this path. A clip that is already
/// H.264 + AAC in an 8-bit pixel format is only remuxed into MP4 with faststart; anything else
/// (notably HEVC/H.265 phone clips) is fully re-encoded to the web-safe profile.
/// </summary>
public interface IVideoTranscoder
{
    /// <summary>Probes <paramref name="inputPath"/> for its duration, codecs and pixel format.</summary>
    Task<VideoProbeResult> ProbeAsync(string inputPath, CancellationToken cancellationToken);

    /// <summary>
    /// Rewraps an already-web-safe clip into MP4 with faststart (<c>-c copy</c>) — no re-encode, so
    /// it is near-instant. Only valid when the source is web-safe (see the normalizer's plan decision).
    /// </summary>
    Task<VideoTranscodeResult> RemuxAsync(string inputPath, string outputPath, CancellationToken cancellationToken);

    /// <summary>
    /// Re-encodes <paramref name="inputPath"/> into <paramref name="outputPath"/> as H.264 (High,
    /// level ≤4.1, yuv420p) + AAC-LC in MP4 with faststart, capped near 720p at the configured target
    /// bitrate. Throws if the tool is missing or the encode fails.
    /// </summary>
    Task<VideoTranscodeResult> TranscodeAsync(string inputPath, string outputPath, CancellationToken cancellationToken);
}
