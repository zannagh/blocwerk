// <copyright file="CaptureFrameSharpness.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using SkiaSharp;

namespace Blocwerk.Core.Capture;

/// <summary>
/// Picks the video frames worth training on: the sharpest (variance of the Laplacian, measured on a
/// small grey copy) of each window of consecutive candidates, then evenly thinned to the frame cap.
/// </summary>
public static class CaptureFrameSharpness
{
    /// <summary>Default long edge the sharpness is measured at: enough for motion blur, cheap to decode.</summary>
    public const int ScoreEdge = 480;

    /// <summary>
    /// Variance of the 4-neighbour Laplacian of the image's luma, measured at about <paramref name="edge"/> px on
    /// the long edge; 0 when it cannot be decoded.
    /// </summary>
    public static double Score(byte[] jpeg, int edge = ScoreEdge)
    {
        using var codec = SKCodec.Create(new MemoryStream(jpeg));
        if (codec is null)
        {
            return 0;
        }

        var scale = Math.Min(1f, Math.Max(1, edge) / (float)Math.Max(codec.Info.Width, codec.Info.Height));
        var size = codec.GetScaledDimensions(scale);
        using var bitmap = new SKBitmap(new SKImageInfo(size.Width, size.Height, SKColorType.Gray8, SKAlphaType.Opaque));
        var result = codec.GetPixels(bitmap.Info, bitmap.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            return 0;
        }

        return LaplacianVariance(bitmap.GetPixelSpan(), bitmap.Width, bitmap.Height, bitmap.RowBytes);
    }

    /// <summary>Variance of the Laplacian over the interior pixels of an 8-bit grey image.</summary>
    public static double LaplacianVariance(ReadOnlySpan<byte> grey, int width, int height, int stride)
    {
        if (width < 3 || height < 3)
        {
            return 0;
        }

        double sum = 0, sumSq = 0;
        long n = 0;
        for (var y = 1; y < height - 1; y++)
        {
            var row = y * stride;
            for (var x = 1; x < width - 1; x++)
            {
                var i = row + x;
                double lap = grey[i - 1] + grey[i + 1] + grey[i - stride] + grey[i + stride] - (4 * grey[i]);
                sum += lap;
                sumSq += lap * lap;
                n++;
            }
        }

        var mean = sum / n;
        return (sumSq / n) - (mean * mean);
    }

    /// <summary>
    /// Indexes (ascending) of the frames to keep: the best-scoring one per window of
    /// <paramref name="window"/> consecutive candidates, thinned evenly to at most <paramref name="max"/>.
    /// </summary>
    public static IReadOnlyList<int> Select(IReadOnlyList<double> scores, int window, int max)
    {
        var picked = new List<int>();
        window = Math.Max(1, window);
        for (var start = 0; start < scores.Count; start += window)
        {
            var best = start;
            for (var i = start + 1; i < Math.Min(scores.Count, start + window); i++)
            {
                if (scores[i] > scores[best])
                {
                    best = i;
                }
            }

            picked.Add(best);
        }

        if (max <= 0 || picked.Count <= max)
        {
            return picked;
        }

        // Evenly spread over the walk, first and last included.
        var step = (picked.Count - 1) / (double)Math.Max(1, max - 1);
        return Enumerable.Range(0, max)
            .Select(k => picked[(int)Math.Round(k * step)])
            .Distinct()
            .ToList();
    }
}
