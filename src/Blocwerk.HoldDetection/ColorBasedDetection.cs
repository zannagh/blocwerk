using Blocwerk.Core.Abstractions;
using Serilog;
using SkiaSharp;

namespace Blocwerk.HoldDetection;

public static class ColorBasedDetection
{
    public static List<DetectedHold> Detect(byte[] imageData, HoldDetectionParameters? parameters = null)
    {
        var p = parameters ?? new HoldDetectionParameters();

        using var bitmap = SKBitmap.Decode(imageData);
        if (bitmap == null)
        {
            return [];
        }

        int w = bitmap.Width;
        int h = bitmap.Height;
        var pixels = bitmap.Pixels;

        var mask = BuildColorMask(pixels, w, h, p.SaturationThreshold);
        Dilate(mask, w, h, 3);
        Erode(mask, w, h, 2);

        var blobs = FindBlobs(mask, w, h, p.MinArea, p.MaxArea);

        var holds = blobs.Select(b =>
        {
            float cx = b.SumX / (float)b.PixelCount;
            float cy = b.SumY / (float)b.PixelCount;
            float radius = MathF.Sqrt(b.PixelCount / MathF.PI);

            var color = ClassifyColor(pixels, w, b);

            return new DetectedHold(
                X: Math.Round(cx / w, 4),
                Y: Math.Round(cy / h, 4),
                Radius: Math.Round(radius / Math.Max(w, h), 4),
                Color: color,
                Confidence: Math.Round(Math.Min(b.Circularity * 1.2, 1.0), 3));
        }).ToList();

        holds.Sort((a, b) => b.Confidence.CompareTo(a.Confidence));

        Log.Information("[Hold Detection] Color-based detection found {Count} holds", holds.Count);
        return holds;
    }

    private static bool[] BuildColorMask(SKColor[] pixels, int w, int h, int satThreshold)
    {
        var mask = new bool[w * h];

        // Compute median brightness of the wall to adapt thresholds
        var brightnessSamples = new List<float>(1000);
        int step = Math.Max(1, (w * h) / 1000);
        for (int i = 0; i < pixels.Length; i += step)
        {
            pixels[i].ToHsv(out _, out _, out float v);
            brightnessSamples.Add(v);
        }

        brightnessSamples.Sort();
        float medianBrightness = brightnessSamples[brightnessSamples.Count / 2];

        for (int i = 0; i < pixels.Length; i++)
        {
            var px = pixels[i];
            px.ToHsv(out float hue, out float sat, out float val);

            // Holds stand out from the wall by being more colorful (higher saturation)
            // or significantly brighter/darker than the wall background
            bool isColorful = sat > satThreshold;
            bool isBright = val > medianBrightness + 25 && sat > 15;
            bool isDark = val < medianBrightness - 35 && val > 10;

            mask[i] = isColorful && (isBright || isDark || sat > satThreshold + 15);
        }

        return mask;
    }

    private static void Dilate(bool[] mask, int w, int h, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            var copy = (bool[])mask.Clone();
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    if (copy[(y * w) + x])
                    {
                        mask[((y - 1) * w) + x] = true;
                        mask[((y + 1) * w) + x] = true;
                        mask[(y * w) + x - 1] = true;
                        mask[(y * w) + x + 1] = true;
                    }
                }
            }
        }
    }

    private static void Erode(bool[] mask, int w, int h, int iterations)
    {
        for (int iter = 0; iter < iterations; iter++)
        {
            var copy = (bool[])mask.Clone();
            for (int y = 1; y < h - 1; y++)
            {
                for (int x = 1; x < w - 1; x++)
                {
                    if (copy[(y * w) + x])
                    {
                        bool allNeighbors = copy[((y - 1) * w) + x]
                                            && copy[((y + 1) * w) + x]
                                            && copy[(y * w) + x - 1]
                                            && copy[(y * w) + x + 1];
                        mask[(y * w) + x] = allNeighbors;
                    }
                }
            }
        }
    }

    private static List<Blob> FindBlobs(bool[] mask, int w, int h, int minArea, int maxArea)
    {
        var labels = new int[w * h];
        var blobs = new List<Blob>();
        int nextLabel = 1;

        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int idx = (y * w) + x;
                if (!mask[idx] || labels[idx] != 0)
                {
                    continue;
                }

                var blob = FloodFill(mask, labels, w, h, x, y, nextLabel);
                nextLabel++;

                if (blob.PixelCount >= minArea && blob.PixelCount <= maxArea)
                {
                    blobs.Add(blob);
                }
            }
        }

        return blobs;
    }

    private static Blob FloodFill(bool[] mask, int[] labels, int w, int h, int startX, int startY, int label)
    {
        var stack = new Stack<(int X, int Y)>();
        stack.Push((startX, startY));

        long sumX = 0, sumY = 0;
        int count = 0;
        int minX = startX, maxX = startX, minY = startY, maxY = startY;

        while (stack.Count > 0)
        {
            var (x, y) = stack.Pop();
            int idx = (y * w) + x;

            if (x < 0 || x >= w || y < 0 || y >= h || !mask[idx] || labels[idx] != 0)
            {
                continue;
            }

            labels[idx] = label;
            sumX += x;
            sumY += y;
            count++;
            if (x < minX)
            {
                minX = x;
            }

            if (x > maxX)
            {
                maxX = x;
            }

            if (y < minY)
            {
                minY = y;
            }

            if (y > maxY)
            {
                maxY = y;
            }

            stack.Push((x + 1, y));
            stack.Push((x - 1, y));
            stack.Push((x, y + 1));
            stack.Push((x, y - 1));
        }

        float bboxArea = (float)(maxX - minX + 1) * (maxY - minY + 1);
        float circularity = bboxArea > 0 ? count / bboxArea : 0;

        return new Blob(sumX, sumY, count, minX, minY, maxX, maxY, circularity);
    }

    private static string ClassifyColor(SKColor[] pixels, int w, Blob blob)
    {
        float cx = blob.SumX / (float)blob.PixelCount;
        float cy = blob.SumY / (float)blob.PixelCount;
        int px = Math.Clamp((int)cx, 0, w - 1);
        int py = Math.Clamp((int)cy, 0, (pixels.Length / w) - 1);

        var color = pixels[(py * w) + px];
        color.ToHsv(out float hue, out float sat, out float val);

        if (sat < 15)
        {
            return val > 70 ? "white" : "gray";
        }

        if (val < 25)
        {
            return "black";
        }

        return hue switch
        {
            < 15 or > 345 => "red",
            < 45 => "orange",
            < 70 => "yellow",
            < 160 => "green",
            < 260 => "blue",
            < 310 => "purple",
            _ => "pink",
        };
    }

    private record Blob(long SumX, long SumY, int PixelCount, int MinX, int MinY, int MaxX, int MaxY, float Circularity);
}
