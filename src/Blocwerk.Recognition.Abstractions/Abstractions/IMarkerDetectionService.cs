namespace Blocwerk.Core.Abstractions;

/// <summary>
/// Detects the printed ArUco "glyph" markers (DICT_4X4_50; ids per the wall's marker plan, or <c>segment*6 + role</c> without one) that
/// anchor wall photos to the measured wall geometry. Every candidate is validated (id range,
/// minimum size, obliqueness, duplicate ids) and rejections are reported, never silently dropped.
/// </summary>
public interface IMarkerDetectionService
{
    /// <summary>Detects and validates markers in an encoded image (JPEG/PNG/WebP bytes).</summary>
    /// <param name="image">The encoded image.</param>
    /// <param name="options">Validation settings; <c>null</c> uses <see cref="MarkerDetectionOptions.Default"/>.</param>
    /// <param name="ct">Cancellation token, checked before and after the (synchronous, CPU-bound) detection.</param>
    Task<MarkerDetectionResult> DetectAsync(byte[] image, MarkerDetectionOptions? options, CancellationToken ct);
}
