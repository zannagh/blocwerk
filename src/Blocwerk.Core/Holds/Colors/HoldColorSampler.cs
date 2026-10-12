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
    public static HoldColorEstimate Estimate(Hold hold, int width, int height, Func<int, int, OklabColor> pixel)
    {
        var mask = HoldColorMask.For(hold, width, height);
        var (left, top, right, bottom) = mask.Bounds(width, height);
        var stride = mask.Stride;
        var own = new List<OklabColor>();
        var surroundings = new List<OklabColor>();
        for (var y = top; y < bottom; y += stride)
        {
            for (var x = left; x < right; x += stride)
            {
                if (mask.IsHold(x + 0.5, y + 0.5))
                {
                    own.Add(pixel(x, y));
                }
                else if (mask.IsSurroundings(x + 0.5, y + 0.5))
                {
                    surroundings.Add(pixel(x, y));
                }
            }
        }

        return HoldColorEstimator.Estimate(own, surroundings, own.Count * (double)stride * stride);
    }
}
