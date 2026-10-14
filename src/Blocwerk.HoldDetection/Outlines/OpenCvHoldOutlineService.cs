using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Helpers;
using Blocwerk.HoldDetection.Segmentation;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Outlines;

/// <summary>
/// OpenCV implementation of <see cref="IHoldOutlineService"/>. Stateless; one decode per session.
/// </summary>
public sealed class OpenCvHoldOutlineService : IHoldOutlineService
{
    private readonly HoldSegmenter? segmenter;

    /// <summary>Initializes a new instance of the <see cref="OpenCvHoldOutlineService"/> class that outlines by colour only.</summary>
    public OpenCvHoldOutlineService()
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenCvHoldOutlineService"/> class.</summary>
    /// <param name="segmenter">The segmentation model; used while its files are there.</param>
    internal OpenCvHoldOutlineService(HoldSegmenter? segmenter)
    {
        this.segmenter = segmenter;
    }

    /// <inheritdoc/>
    public IHoldOutlineSession OpenSession(byte[] encodedImage)
    {
        ArgumentNullException.ThrowIfNull(encodedImage);
        ImagePixelLimit.EnsureDecodable(encodedImage, nameof(encodedImage));

        // IgnoreOrientation: hold X/Y come from the YOLO detector, which decodes with SkiaSharp and does
        // NOT apply EXIF orientation. OpenCV 4.x applies it by default, which would rotate the outline
        // frame away from the seeds on any photo whose EXIF orientation is not 1.
        Mat bgr = Cv2.ImDecode(encodedImage, ImreadModes.Color | ImreadModes.IgnoreOrientation);
        if (bgr.Empty())
        {
            bgr.Dispose();
            throw new ArgumentException("The image could not be decoded.", nameof(encodedImage));
        }

        return new OpenCvHoldOutlineSession(bgr, ownsImage: true, Segmented());
    }

    /// <summary>
    /// Opens a session over an image the caller already decoded (e.g. the overlap matcher's Mat), avoiding
    /// a second decode. The caller keeps ownership of <paramref name="bgr"/> and must outlive the session.
    /// </summary>
    /// <param name="bgr">A decoded 8-bit BGR image.</param>
    /// <returns>The session.</returns>
    public IHoldOutlineSession OpenSession(Mat bgr) => new OpenCvHoldOutlineSession(bgr, ownsImage: false, Segmented());

    private SegmentedOutliner? Segmented() => segmenter is { Available: true } && segmenter.TryLoad() ? new SegmentedOutliner(segmenter) : null;
}
