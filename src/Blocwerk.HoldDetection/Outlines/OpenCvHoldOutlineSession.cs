using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection.Outlines;
using Blocwerk.HoldDetection.Segmentation;
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
public sealed class OpenCvHoldOutlineSession : IHoldBatchOutlineSession
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

    /// <summary>Boundary-to-crop gradient ratio a low-contrast GrabCut outline needs (see TryAcceptByEdges).</summary>
    public const double MinEdgeSupport = 2.2;

    /// <summary>Seeds larger than this (full-image pixels) never take the edge path: they are volumes.</summary>
    public const double MaxEdgePathRadiusPx = 150;

    /// <summary>A low-contrast GrabCut outline this close to the seed ellipse is the seed, not a hold.</summary>
    public const double MaxSeedEllipseIoU = 0.85;

    /// <summary>An outline covering less than this share of its detector box's area is a fragment of the hold.</summary>
    public const double MinBoxArea = 0.25;

    /// <summary>...and so is one spanning less than this share of the box's width or of its height.</summary>
    public const double MinBoxExtent = 0.6;

    private static readonly double[] ThresholdFactors = [0.45, 0.7];

    private readonly Mat image;
    private readonly bool ownsImage;
    private readonly SegmentedOutliner? segmenter;

    /// <summary>Initializes a new instance of the <see cref="OpenCvHoldOutlineSession"/> class.</summary>
    /// <param name="bgr">A decoded 8-bit BGR photo.</param>
    /// <param name="ownsImage">Whether disposing the session disposes <paramref name="bgr"/>.</param>
    public OpenCvHoldOutlineSession(Mat bgr, bool ownsImage)
        : this(bgr, ownsImage, null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="OpenCvHoldOutlineSession"/> class.</summary>
    /// <param name="bgr">A decoded 8-bit BGR photo.</param>
    /// <param name="ownsImage">Whether disposing the session disposes <paramref name="bgr"/>.</param>
    /// <param name="segmenter">The segmentation model for <see cref="OutlineAll"/>; null outlines everything by colour.</param>
    internal OpenCvHoldOutlineSession(Mat bgr, bool ownsImage, SegmentedOutliner? segmenter)
    {
        this.segmenter = segmenter;
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

        // Every consumer (ingest, circle upgrade, wall-update shapes) gets the same smoothed-or-circle result.
        return HoldOutlineRefiner.Refine(OutlineRaw(seed), (double)image.Width / image.Height);
    }

    /// <inheritdoc/>
    public IReadOnlyList<HoldOutlineResult> OutlineAll(IReadOnlyList<HoldSeed> seeds)
    {
        ArgumentNullException.ThrowIfNull(seeds);
        double aspect = (double)image.Width / image.Height;

        // The model answers for the seeds that have a detector box; whatever it cannot outline believably, and a
        // seed that is only a circle, is outlined by colour as before.
        var segmented = segmenter?.Outline(image, seeds) ?? new HoldOutlineResult?[seeds.Count];
        return [.. seeds.Select((seed, i) => Best(seed, segmented[i], aspect))];
    }

    private HoldOutlineResult OutlineRaw(HoldSeed seed)
    {
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

        if (gc is not null && TryAcceptByEdges(gc, crop) is { } edged)
        {
            double conf = Confidence(0.6, cContrast, edged.Ratio);
            return OutlineResultBuilder.FromContour(crop, seed, edged.Contour, [], image.Width, image.Height, conf, HoldOutlineMethod.GrabCut);
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
    /// The first outline that is believable for the seed: the model's, else the colour pass's. With a detector box,
    /// believable means consistent with the box (<see cref="FitsBox"/>): a bolt hole or half a pale hold is a failure,
    /// not a result, and the hold then gets the box's own ellipse as a rough shape instead of a fragment.
    /// </summary>
    private HoldOutlineResult Best(HoldSeed seed, HoldOutlineResult? segmented, double aspect)
    {
        var model = segmented is null ? null : HoldOutlineRefiner.Refine(segmented, aspect);
        if (model is not null && model.Method != HoldOutlineMethod.CircleFallback && FitsBox(seed, model))
        {
            return model;
        }

        var colour = HoldOutlineRefiner.Refine(OutlineRaw(seed), aspect);
        bool boxed = seed.BoxWidth is > 0 && seed.BoxHeight is > 0;
        if (!boxed || (colour.Method != HoldOutlineMethod.CircleFallback && FitsBox(seed, colour)))
        {
            return colour;
        }

        using OutlineCrop? crop = OutlineCrop.Create(image, seed);
        return crop is null
            ? colour
            : HoldOutlineRefiner.Refine(OutlineResultBuilder.Circle(crop, seed, image.Width, image.Height, colour.Confidence / HoldOutlineRefiner.CircleConfidenceCeiling, rough: true), aspect);
    }

    /// <summary>
    /// True when an outline covers enough of its detector box: at least <see cref="MinBoxArea"/> of its area and
    /// <see cref="MinBoxExtent"/> of its width and of its height. Always true for a seed without a box.
    /// </summary>
    private bool FitsBox(HoldSeed seed, HoldOutlineResult result)
    {
        if (seed.BoxWidth is not > 0 || seed.BoxHeight is not > 0)
        {
            return true;
        }

        double boxArea = seed.BoxWidth.Value * image.Width * seed.BoxHeight.Value * image.Height;
        return result.AreaPx >= MinBoxArea * boxArea
            && result.Bounds.Width >= MinBoxExtent * seed.BoxWidth.Value
            && result.Bounds.Height >= MinBoxExtent * seed.BoxHeight.Value;
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
    /// The low-contrast GrabCut path: a pale hold on pale wood (white, grey, beige) has too little COLOUR
    /// contrast for the colour pass, but a clear outline. GrabCut's answer is kept only when its boundary sits
    /// on real edges, the seed is hold-sized (not a volume), (<see cref="MaskOps.EdgeSupport"/> ≥ <see cref="MinEdgeSupport"/>) and it is not simply the
    /// seed ellipse handed back — which is what GrabCut returns on a featureless, wall-coloured volume.
    /// </summary>
    private static (Point[] Contour, double Ratio)? TryAcceptByEdges(Mat gc, OutlineCrop crop)
    {
        // Wooden volumes are the one wall-coloured object with strong edges, and they are big: by colour and
        // edges alone a volume is indistinguishable from a pale hold, so the edge path leaves large seeds alone.
        if (crop.Radius / crop.Scale > MaxEdgePathRadiusPx)
        {
            return null;
        }

        using Mat mask = gc.Clone();
        MaskOps.Clean(mask, crop.Radius);
        Point[]? contour = MaskOps.SeedContour(mask, crop);
        if (contour is null)
        {
            return null;
        }

        var (ok, ratio, _) = MaskOps.Assess(contour, crop, crop.HasBox ? MaskOps.MinAreaRatioBoxed : MaskOps.MinAreaRatio);
        if (!ok || ratio > 1.15 || MaskOps.SeedEllipseIoU(contour, crop) > MaxSeedEllipseIoU)
        {
            return null;
        }

        // A wooden volume is plywood: its outline is a real edge, but inside it has the wall's chromaticity.
        // A pale hold differs from the wall in chromaticity even where its weighted Lab contrast is tiny.
        return MaskOps.EdgeSupport(contour, crop) >= MinEdgeSupport ? (contour, ratio) : null;
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
