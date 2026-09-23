using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Outlines;

/// <summary>
/// Outlines holds on one decoded photo. Per hold: cut a working crop around the seed → sample the local
/// wall and hold colours → colour-distance mask (shadows suppressed) → morphology → seed component →
/// size/leak checks; on failure retry with a stricter threshold, then GrabCut, then give up honestly with
/// the seed circle at low confidence. An accepted outline keeps its significant interior holes (pocket and
/// donut holds, see <see cref="HoleFinder"/>); every acceptance test still runs on the filled outline.
/// </summary>
/// <remarks>
/// Guards against leaking blobs, in order: a hold/wall contrast gate (<see cref="MinContrast"/>); pixels must
/// lean towards the hold colour, so a touching neighbour of another colour is not swallowed; darker pixels
/// with the wall's chromaticity (shadows, shaded volume faces) are wall; an outline must stay inside 1.8 seed
/// radii and within 0.12 (circle seed) / 0.3 (box seed) … 1.45 seed areas; at most 40 % of it may look like
/// wall; GrabCut only runs for contrast ≥ <see cref="MinGrabCutContrast"/>. Wooden volumes the colour of the
/// wall therefore end as <see cref="HoldOutlineMethod.CircleFallback"/>.
/// </remarks>
public sealed class OpenCvHoldOutlineSession : IHoldOutlineSession
{
    /// <summary>Hold/wall contrast (lightness-weighted ΔE) below which no segmentation is attempted.</summary>
    public const double MinContrast = 9;

    /// <summary>An outline whose pixels are more than this share wall-coloured is the wall (or a volume), not a hold.</summary>
    public const double MaxWallLikeShare = 0.4;

    /// <summary>
    /// GrabCut is only trusted on a clearly distinct hold: its init marks the whole seed as probable
    /// foreground, so on a wall-coloured volume it happily "succeeds" with the seed ellipse.
    /// </summary>
    public const double MinGrabCutContrast = 18;

    private static readonly double[] ThresholdFactors = [0.45, 0.7];

    private readonly Mat image;
    private readonly bool ownsImage;

    /// <summary>Initializes a new instance of the <see cref="OpenCvHoldOutlineSession"/> class.</summary>
    /// <param name="bgr">A decoded 8-bit BGR photo.</param>
    /// <param name="ownsImage">Whether disposing the session disposes <paramref name="bgr"/>.</param>
    public OpenCvHoldOutlineSession(Mat bgr, bool ownsImage)
    {
        ArgumentNullException.ThrowIfNull(bgr);
        if (bgr.Empty() || bgr.Type() != MatType.CV_8UC3)
        {
            throw new ArgumentException("Expected a non-empty 8-bit BGR image.", nameof(bgr));
        }

        image = bgr;
        this.ownsImage = ownsImage;
    }

    /// <inheritdoc/>
    public int ImageWidth => image.Width;

    /// <inheritdoc/>
    public int ImageHeight => image.Height;

    /// <inheritdoc/>
    public HoldOutlineResult Outline(HoldSeed seed)
    {
        ArgumentNullException.ThrowIfNull(seed);
        using OutlineCrop? crop = OutlineCrop.Create(image, seed);
        if (crop is null)
        {
            return OutlineResultBuilder.Degenerate(seed);
        }

        var px = CropPixels.From(crop.Bgr);
        LocalColourModel? model = LocalColourModel.Sample(px, crop);
        double cContrast = model is null ? 0 : Math.Clamp((model.Contrast - MinContrast) / 22.0, 0, 1);
        if (model is null || model.Contrast < MinContrast)
        {
            return OutlineResultBuilder.Circle(crop, seed, image.Width, image.Height, cContrast);
        }

        for (int k = 0; k < ThresholdFactors.Length; k++)
        {
            using Mat raw = ColourDistanceSegmenter.Segment(px, model, ThresholdFactors[k]);
            var accepted = TryAccept(raw, crop, px, model);
            if (accepted is { } ok)
            {
                double conf = Confidence(k == 0 ? 1.0 : 0.9, cContrast, ok.Ratio);
                return OutlineResultBuilder.FromContour(crop, seed, ok.Contour, ok.Holes, image.Width, image.Height, conf, HoldOutlineMethod.Contour);
            }
        }

        using Mat? gc = GrabCutSegmenter.Segment(crop);
        if (gc is not null && model.Contrast >= MinGrabCutContrast && TryAccept(gc, crop, px, model) is { } cut && cut.Ratio <= 1.25)
        {
            double conf = Confidence(0.75, cContrast, cut.Ratio);
            return OutlineResultBuilder.FromContour(crop, seed, cut.Contour, cut.Holes, image.Width, image.Height, conf, HoldOutlineMethod.GrabCut);
        }

        return OutlineResultBuilder.Circle(crop, seed, image.Width, image.Height, cContrast);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (ownsImage)
        {
            image.Dispose();
        }
    }

    /// <summary>
    /// Confidence = method factor × (0.3 + 0.7 × (0.55 · contrast + 0.45 · area plausibility)), where area
    /// plausibility is 1 for 0.3–1.05 seed areas and falls off linearly towards the rejection limits.
    /// </summary>
    private static double Confidence(double methodFactor, double cContrast, double ratio)
    {
        double cArea = ratio < 0.3
            ? ratio / 0.3
            : ratio > 1.05 ? Math.Clamp((MaskOps.MaxAreaRatio - ratio) / (MaskOps.MaxAreaRatio - 1.05), 0, 1) : 1;
        double raw = methodFactor * (0.3 + (0.7 * ((0.55 * cContrast) + (0.45 * cArea))));
        return Math.Round(Math.Clamp(raw, 0.21, 0.97), 3);
    }

    /// <summary>
    /// Cleans the mask, picks the seed component and checks it. The acceptance tests (size, leak, wall share) run
    /// on the FILLED outline exactly as before holes were kept, so pocket support cannot change which holds are
    /// outlined; the interior holes are only looked for once the outline is accepted (<see cref="HoleFinder"/>).
    /// </summary>
    private static (Point[] Contour, double Ratio, IReadOnlyList<Point[]> Holes)? TryAccept(Mat mask, OutlineCrop crop, CropPixels px, LocalColourModel model)
    {
        MaskOps.Clean(mask, crop.Radius);
        Point[]? contour = MaskOps.SeedContour(mask, crop);
        if (contour is null)
        {
            return null;
        }

        var (ok, ratio, _) = MaskOps.Assess(contour, crop, crop.HasBox ? MaskOps.MinAreaRatioBoxed : MaskOps.MinAreaRatio);
        if (!ok)
        {
            return null;
        }

        using Mat filled = MaskOps.Fill(contour, crop.Bgr.Width, crop.Bgr.Height);
        float wallShare = model.WallLikeShare(px, CropPixels.ToBytes(filled), (float)MinContrast);
        return wallShare <= MaxWallLikeShare ? (contour, ratio, HoleFinder.Find(mask, contour, px, model)) : null;
    }
}
