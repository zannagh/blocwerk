// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.Core.Abstractions;

namespace Blocwerk.Core.Recognition.Remote;

/// <summary>
/// The outline of a seed a runner could not outline: the circle fallback the in-process outliner gives such a seed, the seed
/// circle as a 16-gon at the lowest fallback confidence. Its fingerprint stays empty: the app has no pixels to measure it on
/// (the outliner's off-photo fallback carries an empty one too).
/// </summary>
public static class SeedCircleOutline
{
    /// <summary>The lowest confidence the in-process outliner gives a circle fallback (no contrast at all).</summary>
    public const double Confidence = 0.05;

    public static HoldOutlineResult Of(HoldSeed seed, int imageWidth, int imageHeight)
    {
        var radiusPx = seed.Radius * Math.Max(imageWidth, imageHeight);
        var polygon = Enumerable.Range(0, 16)
            .Select(k => new NormalizedPoint(
                seed.X + (radiusPx * Math.Cos(k * Math.PI / 8) / imageWidth),
                seed.Y + (radiusPx * Math.Sin(k * Math.PI / 8) / imageHeight)))
            .ToList();
        var (rx, ry) = (radiusPx / imageWidth, radiusPx / imageHeight);
        return new HoldOutlineResult(
            polygon,
            seed.X,
            seed.Y,
            null,
            Math.PI * radiusPx * radiusPx,
            new HoldOutlineBounds(seed.X - rx, seed.Y - ry, 2 * rx, 2 * ry),
            Confidence,
            HoldOutlineMethod.CircleFallback,
            new HoldFingerprint());
    }
}
