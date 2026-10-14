// <copyright file="HoldColorSampler.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Holds.Colors;

/// <summary>Reads a hold's pixels and its surroundings off a photo and hands them to <see cref="HoldColorEstimator"/>.</summary>
internal static class HoldColorSampler
{
    /// <summary>Estimates the colour of one hold on a photo.</summary>
    /// <param name="hold">The hold, positioned on this photo.</param>
    /// <param name="width">Photo width in pixels.</param>
    /// <param name="height">Photo height in pixels.</param>
    /// <param name="pixel">The photo's pixel at (x, y).</param>
    /// <returns>The estimate.</returns>
    public static HoldColorEstimate Estimate(Hold hold, int width, int height, Func<int, int, OklabColor> pixel) =>
        Estimate(hold, width, height, pixel, null);

    /// <summary>Estimates the colour of one hold on a photo, leaving out the pixels the given test excludes.</summary>
    /// <param name="hold">The hold, positioned on this photo.</param>
    /// <param name="width">Photo width in pixels.</param>
    /// <param name="height">Photo height in pixels.</param>
    /// <param name="pixel">The photo's pixel at (x, y).</param>
    /// <param name="excluded">True for a pixel (x, y in photo fractions) that does not belong to the hold: a hold screwed onto it.</param>
    /// <returns>The estimate.</returns>
    public static HoldColorEstimate Estimate(Hold hold, int width, int height, Func<int, int, OklabColor> pixel, Func<double, double, bool>? excluded)
    {
        var (own, surroundings, area) = Sample(hold, width, height, pixel, excluded);
        return HoldColorEstimator.Estimate(own, surroundings, area);
    }

    /// <summary>The estimate of one hold together with its <see cref="HoldColorSignature"/> (null when too little of it is coloured).</summary>
    /// <param name="hold">The hold, positioned on this photo.</param>
    /// <param name="width">Photo width in pixels.</param>
    /// <param name="height">Photo height in pixels.</param>
    /// <param name="pixel">The photo's pixel at (x, y).</param>
    /// <returns>The estimate and the signature.</returns>
    public static (HoldColorEstimate Estimate, HoldColorSignature? Signature) Read(Hold hold, int width, int height, Func<int, int, OklabColor> pixel)
    {
        var (own, surroundings, area) = Sample(hold, width, height, pixel);
        return (HoldColorEstimator.Estimate(own, surroundings, area), HoldColorSignature.Of(own, surroundings));
    }

    /// <summary>The pixels <see cref="Read"/> reads: the hold's, its surroundings' and the hold's area in pixels.</summary>
    /// <param name="hold">The hold, positioned on this photo.</param>
    /// <param name="width">Photo width in pixels.</param>
    /// <param name="height">Photo height in pixels.</param>
    /// <param name="pixel">The photo's pixel at (x, y).</param>
    /// <param name="excluded">True for a pixel (x, y in photo fractions) that does not belong to the hold; null for none.</param>
    /// <returns>The samples.</returns>
    public static (List<OklabColor> Own, List<OklabColor> Surroundings, double Area) Sample(
        Hold hold, int width, int height, Func<int, int, OklabColor> pixel, Func<double, double, bool>? excluded = null)
    {
        var mask = HoldColorMask.For(hold, width, height);
        var (left, top, right, bottom) = mask.Bounds(width, height);
        var stride = mask.Stride;
        var own = new List<OklabColor>();
        var rim = new List<OklabColor>();
        var surroundings = new List<OklabColor>();
        for (var y = top; y < bottom; y += stride)
        {
            for (var x = left; x < right; x += stride)
            {
                if (excluded is not null && excluded((x + 0.5) / width, (y + 0.5) / height))
                {
                    continue;
                }

                if (mask.IsHold(x + 0.5, y + 0.5))
                {
                    own.Add(pixel(x, y));
                }
                else if (mask.IsOnHold(x + 0.5, y + 0.5))
                {
                    rim.Add(pixel(x, y));
                }
                else if (mask.IsSurroundings(x + 0.5, y + 0.5))
                {
                    surroundings.Add(pixel(x, y));
                }
            }
        }

        // An outline too thin to keep a core after its rim is taken off is read whole.
        if (own.Count < HoldColorEstimator.MinPixels)
        {
            own.AddRange(rim);
        }

        return (own, surroundings, own.Count * (double)stride * stride);
    }
}
