namespace Blocwerk.Core.Abstractions;

/// <summary>The outcome of a transcode: the new file's size and MIME type.</summary>
public record VideoTranscodeResult(long SizeBytes, string ContentType);

/// <summary>
/// Re-encodes an oversized clip down toward a target file size. Implemented with ffmpeg, which the
/// runtime image ships (see docker/Dockerfile) precisely for this path.
/// </summary>
public interface IVideoTranscoder
{
    /// <summary>
    /// Re-encodes <paramref name="inputPath"/> into <paramref name="outputPath"/> aiming for roughly
    /// <paramref name="targetBytes"/>. Throws if the tool is missing or the encode fails.
    /// </summary>
    Task<VideoTranscodeResult> ShrinkAsync(string inputPath, string outputPath, long targetBytes, CancellationToken cancellationToken);
}
