using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Helpers;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Markers;

/// <summary>
/// In-process OpenCV ArUco implementation of <see cref="IMarkerDetectionService"/>. Stateless,
/// safe as a singleton. Runs on the full-resolution grayscale image: markers on the wall photos
/// are as small as ~42 px, so downscaling first would cost the corner markers we cannot lose.
/// </summary>
public sealed class ArucoMarkerDetectionService : IMarkerDetectionService
{
    /// <inheritdoc/>
    public Task<MarkerDetectionResult> DetectAsync(byte[] image, MarkerDetectionOptions? options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(image);
        ct.ThrowIfCancellationRequested();
        ImagePixelLimit.EnsureDecodable(image, nameof(image));

        // IgnoreOrientation keeps marker corners in the raw pixel grid the hold detector (SkiaSharp,
        // EXIF-unaware) stores hold X/Y in; OpenCV 4.x would otherwise apply the EXIF rotation.
        using var gray = Cv2.ImDecode(image, ImreadModes.Grayscale | ImreadModes.IgnoreOrientation);
        if (gray.Empty())
        {
            throw new ArgumentException("Could not decode the image.", nameof(image));
        }

        var result = Detect(gray, options ?? MarkerDetectionOptions.Default);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(result);
    }

    /// <summary>Detects and validates markers in an already-decoded 8-bit grayscale image.</summary>
    internal static MarkerDetectionResult Detect(Mat gray, MarkerDetectionOptions options)
    {
        using var dictionary = ArucoInterop.CreateDict4X4With50();
        var parameters = ArucoInterop.CreateTunedParameters();
        ArucoInterop.Detect(gray, dictionary, parameters, out var corners, out var ids, out var undecoded);

        var candidates = new List<MarkerCandidate>(ids.Length);
        for (var i = 0; i < ids.Length; i++)
        {
            var quad = corners[i].Select(p => new MarkerPoint(p.X, p.Y)).ToList();
            candidates.Add(new MarkerCandidate(ids[i], quad));
        }

        var outcome = MarkerCandidateValidator.Validate(candidates, options, gray.Width, gray.Height);

        // ArUco's own corners snap to the paper edge or the corner screws (3–29 px off on real walls);
        // every millimetre downstream depends on them, so re-fit the black square's sides.
        var markers = options.RefineCorners ? MarkerCornerRefiner.RefineAll(gray, outcome.Accepted) : outcome.Accepted;
        return new MarkerDetectionResult
        {
            ImageWidth = gray.Width,
            ImageHeight = gray.Height,
            Markers = markers,
            Rejected = outcome.Rejected,
            UndecodedCandidateCount = undecoded.Length,
            Suspicious = outcome.Suspicious,
            Warnings = outcome.Warnings,
        };
    }
}
