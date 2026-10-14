// <copyright file="OutlineChoice.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Abstractions;
using OpenCvSharp;

namespace Blocwerk.HoldDetection.Outlines;

/// <summary>
/// Chooses between the two outlines of one hold: the segmentation model's and the colour pass's. The rule is that the
/// result is never worse than the colour pass alone, which is what every hold got before the model existed.
/// <list type="bullet">
/// <item>An outline that covers only a fragment of the detector box (<see cref="FitsBox"/>) is no candidate.</item>
/// <item>Where both agree (<see cref="AgreeIoU"/>, <see cref="AgreeArea"/>) the model's is taken and marked
/// corroborated: two independent methods found the same shape.</item>
/// <item>Where they disagree clearly, one lying inside the other, the image decides: the outline whose boundary sits
/// on stronger edges. The model's has to reach <see cref="MinEdgeShare"/> of the colour outline's edge strength;
/// (<see cref="MinEdgeShareWhenLarger"/> when it is the larger one); below it the model grew over a neighbour or a surrounding hold, or cut off a rim or the second colour of a
/// two-tone hold, and the colour outline stays.</item>
/// <item>With neither, the hold gets the box's ellipse as a rough shape, never a fragment.</item>
/// </list>
/// Only a corroborated outline (both methods agree) counts as safe to accept without a look, and for a hold that has
/// only a circle so far the colour pass also has to be sure of its own (<see cref="ConfirmingConfidence"/>).
/// </summary>
internal sealed class OutlineChoice
{
    /// <summary>An outline covering less than this share of its detector box's area is a fragment of the hold.</summary>
    public const double MinBoxArea = 0.25;

    /// <summary>...and so is one spanning less than this share of the box's width or of its height.</summary>
    public const double MinBoxExtent = 0.6;

    /// <summary>An outline spanning at least this share of the box's width and height is taken as the whole hold.</summary>
    public const double FullBoxCover = 0.85;

    /// <summary>Two outlines agree from this overlap (IoU)...</summary>
    public const double AgreeIoU = 0.75;

    /// <summary>...when the model's area is within this factor of the colour outline's, either way.</summary>
    public const double AgreeArea = 1.2;

    /// <summary>Two outlines disagree clearly when one is this many times the other's area...</summary>
    public const double DisagreeArea = 1.33;

    /// <summary>...and the smaller one lies inside the larger by at least this share of its own area.</summary>
    public const double DisagreeInside = 0.85;

    /// <summary>
    /// From this confidence of the colour pass the hold counts as standing out from the wall (traced at the first
    /// threshold, clear contrast). On a pale or chalked hold two methods agreeing proves little.
    /// </summary>
    public const double ConfirmingConfidence = 0.8;

    /// <summary>A model outline clearly smaller than the colour outline needs this share of its edge strength.</summary>
    public const double MinEdgeShare = 0.75;

    /// <summary>
    /// A model outline clearly LARGER than the colour outline needs more: growing over the hold around a pale patch or
    /// a pocket, or over a neighbour, is the failure that costs most, and it shows as a boundary on weaker edges.
    /// </summary>
    public const double MinEdgeShareWhenLarger = 0.9;

    private readonly Mat image;

    /// <summary>Initializes a new instance of the <see cref="OutlineChoice"/> class.</summary>
    /// <param name="image">The full BGR photo.</param>
    public OutlineChoice(Mat image)
    {
        this.image = image;
    }

    /// <summary>The outline of one hold.</summary>
    /// <param name="seed">The seed.</param>
    /// <param name="model">The model's outline, refined; null when it gave none.</param>
    /// <param name="colour">The colour pass's outline, refined.</param>
    /// <param name="modelAsked">False when there is no model at all: the colour outline then counts as before.</param>
    /// <returns>The chosen outline, or null when neither is believable and the seed has a box (the caller makes the rough shape).</returns>
    public HoldOutlineResult? Pick(HoldSeed seed, HoldOutlineResult? model, HoldOutlineResult colour, bool modelAsked)
    {
        bool boxed = seed.BoxWidth is > 0 && seed.BoxHeight is > 0;
        bool modelFits = model is not null && model.Method != HoldOutlineMethod.CircleFallback && FitsBox(seed, model);
        bool colourFits = colour.Method != HoldOutlineMethod.CircleFallback && FitsBox(seed, colour);
        if (!modelFits)
        {
            // As before the model: the colour outline, corroborated by nothing but also changed by nothing.
            return colourFits || !boxed ? colour with { Corroborated = !modelAsked && colourFits, Distinct = colour.Confidence >= ConfirmingConfidence } : null;
        }

        if (!colourFits)
        {
            // Only one method traced it: never confirmed, whatever it covers.
            return model! with { Corroborated = false };
        }

        var (iou, modelInside, colourInside) = Overlap(model!, colour);
        double ratio = model!.AreaPx / Math.Max(1, colour.AreaPx);
        if (iou >= AgreeIoU && ratio <= AgreeArea && ratio >= 1 / AgreeArea)
        {
            return model with { Corroborated = true, Distinct = colour.Confidence >= ConfirmingConfidence };
        }

        bool clearly = (ratio >= DisagreeArea && colourInside >= DisagreeInside) || (ratio <= 1 / DisagreeArea && modelInside >= DisagreeInside);
        double needed = ratio > 1 ? MinEdgeShareWhenLarger : MinEdgeShare;
        if (clearly && EdgeStrength(seed, model) < needed * EdgeStrength(seed, colour))
        {
            return colour;
        }

        // A milder difference: the one that spans the detector box is the hold.
        return BoxCover(seed, model) + 0.1 <= BoxCover(seed, colour) && BoxCover(seed, model) < FullBoxCover ? colour : model;
    }

    /// <summary>
    /// True when an outline covers enough of its detector box: at least <see cref="MinBoxArea"/> of its area and
    /// <see cref="MinBoxExtent"/> of its width and of its height. Always true for a seed without a box.
    /// </summary>
    /// <param name="seed">The seed.</param>
    /// <param name="result">The outline.</param>
    /// <returns>Whether it fits.</returns>
    public bool FitsBox(HoldSeed seed, HoldOutlineResult result)
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

    /// <summary>The smaller of the shares of the box's width and of its height that the outline spans; 1 without a box.</summary>
    private static double BoxCover(HoldSeed seed, HoldOutlineResult result) =>
        seed.BoxWidth is > 0 && seed.BoxHeight is > 0
            ? Math.Min(result.Bounds.Width / seed.BoxWidth.Value, result.Bounds.Height / seed.BoxHeight.Value)
            : 1;

    /// <summary>IoU of two outlines and the share of each inside the other, on a raster of at most 160 px.</summary>
    private (double Iou, double AInside, double BInside) Overlap(HoldOutlineResult a, HoldOutlineResult b)
    {
        double minX = Math.Min(a.Bounds.X, b.Bounds.X), minY = Math.Min(a.Bounds.Y, b.Bounds.Y);
        double maxX = Math.Max(a.Bounds.X + a.Bounds.Width, b.Bounds.X + b.Bounds.Width);
        double maxY = Math.Max(a.Bounds.Y + a.Bounds.Height, b.Bounds.Y + b.Bounds.Height);
        double scale = 160 / Math.Max(1e-9, Math.Max((maxX - minX) * image.Width, (maxY - minY) * image.Height));
        Point[] Pixels(HoldOutlineResult r) => [.. r.Polygon.Select(p =>
            new Point((int)Math.Round((p.X - minX) * image.Width * scale), (int)Math.Round((p.Y - minY) * image.Height * scale)))];
        using Mat ma = MaskOps.Fill(Pixels(a), 162, 162);
        using Mat mb = MaskOps.Fill(Pixels(b), 162, 162);
        using var both = new Mat();
        Cv2.BitwiseAnd(ma, mb, both);
        double shared = Cv2.CountNonZero(both), areaA = Math.Max(1, Cv2.CountNonZero(ma)), areaB = Math.Max(1, Cv2.CountNonZero(mb));
        return (shared / (areaA + areaB - shared), shared / areaA, shared / areaB);
    }

    /// <summary>
    /// How strongly the outline's boundary sits on image edges, relative to the texture around the hold
    /// (<see cref="MaskOps.EdgeSupport"/>, sampled along the whole boundary); 0 when the seed is off the image.
    /// </summary>
    private double EdgeStrength(HoldSeed seed, HoldOutlineResult result)
    {
        using OutlineCrop? crop = OutlineCrop.Create(image, seed);
        if (crop is null)
        {
            return 0;
        }

        var points = new List<Point>();
        for (int i = 0; i < result.Polygon.Count; i++)
        {
            var (from, to) = (ToCrop(crop, result.Polygon[i]), ToCrop(crop, result.Polygon[(i + 1) % result.Polygon.Count]));
            int steps = Math.Max(1, (int)Math.Ceiling(from.DistanceTo(to) / 1.5));
            for (int k = 0; k < steps; k++)
            {
                points.Add(new Point(
                    (int)Math.Round(from.X + ((to.X - from.X) * k / steps)),
                    (int)Math.Round(from.Y + ((to.Y - from.Y) * k / steps))));
            }
        }

        return MaskOps.EdgeSupport([.. points], crop);
    }

    private Point2d ToCrop(OutlineCrop crop, NormalizedPoint p) =>
        new(((p.X * image.Width) - crop.Roi.X) * crop.Scale, ((p.Y * image.Height) - crop.Roi.Y) * crop.Scale);
}
