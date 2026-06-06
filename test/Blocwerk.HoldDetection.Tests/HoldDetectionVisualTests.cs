using Blocwerk.Core.Abstractions;
using SkiaSharp;

namespace Blocwerk.HoldDetection.Tests;

public class HoldDetectionVisualTests : IDisposable
{
    private static readonly string WallsDir = Path.Combine(AppContext.BaseDirectory, "walls");
    private static readonly string OutputDir = Path.Combine(AppContext.BaseDirectory, "walls", "output");
    private readonly YoloHoldDetectionService _service;

    public HoldDetectionVisualTests()
    {
        var modelPath = HoldDetectionServices.ResolveModelPath("models/yolov8s.onnx");
        _service = new YoloHoldDetectionService(modelPath);
    }

    [SkippableFact]
    public async Task DetectHolds_AllWallImages_OutputAnnotated()
    {
        Directory.CreateDirectory(OutputDir);

        var imageFiles = Directory.GetFiles(WallsDir)
            .Where(f => IsImageFile(f))
            .ToList();

        Skip.If(imageFiles.Count == 0, "No wall images found in walls/ directory");

        foreach (var imagePath in imageFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(imagePath);
            var imageData = await File.ReadAllBytesAsync(imagePath);

            var holds = await _service.DetectHoldsAsync(imageData);

            var annotatedPath = Path.Combine(OutputDir, $"{fileName}_detected.png");
            RenderAnnotatedImage(imageData, holds, annotatedPath);

            var summaryPath = Path.Combine(OutputDir, $"{fileName}_summary.txt");
            await WriteSummary(summaryPath, imagePath, holds);
        }
    }

    [SkippableFact]
    public async Task DetectHolds_AllWallImages_WithLowThreshold()
    {
        Directory.CreateDirectory(OutputDir);

        var imageFiles = Directory.GetFiles(WallsDir)
            .Where(f => IsImageFile(f))
            .ToList();

        Skip.If(imageFiles.Count == 0, "No wall images found in walls/ directory");

        var parameters = new HoldDetectionParameters(MinArea: 100, MaxArea: 80000, BlurSize: 3, SaturationThreshold: 20);

        foreach (var imagePath in imageFiles)
        {
            var fileName = Path.GetFileNameWithoutExtension(imagePath);
            var imageData = await File.ReadAllBytesAsync(imagePath);

            var holds = await _service.DetectHoldsAsync(imageData, parameters);

            var annotatedPath = Path.Combine(OutputDir, $"{fileName}_low_threshold.png");
            RenderAnnotatedImage(imageData, holds, annotatedPath);

            var summaryPath = Path.Combine(OutputDir, $"{fileName}_low_threshold_summary.txt");
            await WriteSummary(summaryPath, imagePath, holds);
        }
    }

    private static void RenderAnnotatedImage(byte[] imageData, List<DetectedHold> holds, string outputPath)
    {
        using var original = SKBitmap.Decode(imageData);
        using var surface = SKSurface.Create(new SKImageInfo(original.Width, original.Height));
        var canvas = surface.Canvas;

        canvas.DrawBitmap(original, 0, 0);

        var fillPaint = new SKPaint
        {
            Color = new SKColor(229, 69, 96, 80),
            Style = SKPaintStyle.Fill,
            IsAntialias = true,
        };

        var strokePaint = new SKPaint
        {
            Color = new SKColor(229, 69, 96),
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 2,
            IsAntialias = true,
        };

        var textPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = 14,
            IsAntialias = true,
        };

        var textBgPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 180),
            Style = SKPaintStyle.Fill,
        };

        for (int i = 0; i < holds.Count; i++)
        {
            var hold = holds[i];
            float cx = (float)(hold.X * original.Width);
            float cy = (float)(hold.Y * original.Height);
            float r = (float)(hold.Radius * Math.Max(original.Width, original.Height));

            canvas.DrawCircle(cx, cy, r, fillPaint);
            canvas.DrawCircle(cx, cy, r, strokePaint);

            var label = $"#{i + 1} {hold.Confidence:P0}";
            if (hold.Color != null)
            {
                label += $" {hold.Color}";
            }

            var textBounds = new SKRect();
            textPaint.MeasureText(label, ref textBounds);
            canvas.DrawRect(cx - 2, cy - r - textBounds.Height - 6, textBounds.Width + 6, textBounds.Height + 4, textBgPaint);
            canvas.DrawText(label, cx, cy - r - 4, textPaint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }

    private static async Task WriteSummary(string path, string imagePath, List<DetectedHold> holds)
    {
        var lines = new List<string>
        {
            $"Image: {Path.GetFileName(imagePath)}",
            $"Total holds detected: {holds.Count}",
            $"---",
        };

        for (int i = 0; i < holds.Count; i++)
        {
            var h = holds[i];
            lines.Add($"Hold #{i + 1}: pos=({h.X:F4}, {h.Y:F4}) r={h.Radius:F4} conf={h.Confidence:P1} color={h.Color ?? "unknown"}");
        }

        await File.WriteAllLinesAsync(path, lines);
    }

    private static bool IsImageFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".heic" or ".webp" or ".bmp";
    }

    public void Dispose()
    {
        _service.Dispose();
    }
}
