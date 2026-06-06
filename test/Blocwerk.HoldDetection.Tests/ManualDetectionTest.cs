using Blocwerk.Core.Abstractions;
using SkiaSharp;

namespace Blocwerk.HoldDetection.Tests;

public class ManualDetectionTest
{
    private static readonly string WallsDir = Path.Combine(AppContext.BaseDirectory, "walls");
    private static readonly string OutputDir = Path.Combine(AppContext.BaseDirectory, "walls", "output");

    /// <summary>
    /// Manually identified holds from visual inspection of Test-Wall.jpeg.
    /// Coordinates are normalized (0-1), origin top-left.
    /// </summary>
    private static List<DetectedHold> GetManualHolds() =>
    [
        // Top row — large white/cream jugs near ceiling
        new(0.53, 0.06, 0.020, "white", 1.0),
        new(0.60, 0.05, 0.018, "white", 1.0),
        new(0.67, 0.07, 0.022, "white", 1.0),
        new(0.75, 0.06, 0.020, "white", 1.0),

        // Upper-left area
        new(0.18, 0.17, 0.018, "white", 1.0),
        new(0.24, 0.14, 0.012, "green", 1.0),
        new(0.13, 0.22, 0.015, "yellow", 1.0),
        new(0.20, 0.25, 0.016, "yellow", 1.0),

        // Upper-center area
        new(0.30, 0.20, 0.014, "yellow", 1.0),
        new(0.38, 0.18, 0.013, "yellow", 1.0),
        new(0.42, 0.22, 0.012, "pink", 1.0),
        new(0.48, 0.20, 0.015, "yellow", 1.0),
        new(0.35, 0.24, 0.013, "blue", 1.0),
        new(0.52, 0.18, 0.014, "pink", 1.0),

        // Upper-right area
        new(0.62, 0.16, 0.013, "yellow", 1.0),
        new(0.68, 0.18, 0.012, "pink", 1.0),
        new(0.72, 0.15, 0.010, "green", 1.0),
        new(0.78, 0.22, 0.025, "white", 1.0),  // large triangular volume
        new(0.85, 0.18, 0.012, "purple", 1.0),
        new(0.88, 0.20, 0.012, "yellow", 1.0),

        // Middle-left
        new(0.08, 0.32, 0.018, "white", 1.0),
        new(0.10, 0.38, 0.015, "yellow", 1.0),
        new(0.16, 0.35, 0.014, "yellow", 1.0),
        new(0.22, 0.30, 0.016, "yellow", 1.0),
        new(0.15, 0.42, 0.013, "pink", 1.0),

        // Middle-center — dense cluster
        new(0.28, 0.33, 0.015, "white", 1.0),
        new(0.32, 0.30, 0.013, "yellow", 1.0),
        new(0.36, 0.35, 0.014, "pink", 1.0),
        new(0.40, 0.32, 0.018, "yellow", 1.0),
        new(0.44, 0.28, 0.015, "white", 1.0),
        new(0.38, 0.38, 0.016, "orange", 1.0),
        new(0.42, 0.36, 0.014, "blue", 1.0),
        new(0.34, 0.40, 0.012, "green", 1.0),
        new(0.48, 0.34, 0.013, "yellow", 1.0),
        new(0.46, 0.40, 0.015, "white", 1.0),
        new(0.50, 0.38, 0.014, "blue", 1.0),

        // Middle-right
        new(0.56, 0.30, 0.018, "white", 1.0),
        new(0.60, 0.35, 0.014, "yellow", 1.0),
        new(0.65, 0.32, 0.015, "blue", 1.0),
        new(0.70, 0.28, 0.012, "pink", 1.0),
        new(0.72, 0.35, 0.013, "yellow", 1.0),
        new(0.78, 0.33, 0.015, "white", 1.0),
        new(0.82, 0.30, 0.014, "yellow", 1.0),
        new(0.85, 0.35, 0.013, "brown", 1.0),

        // Lower-middle
        new(0.25, 0.45, 0.016, "yellow", 1.0),
        new(0.30, 0.48, 0.014, "pink", 1.0),
        new(0.35, 0.45, 0.013, "blue", 1.0),
        new(0.40, 0.50, 0.015, "white", 1.0),
        new(0.45, 0.48, 0.014, "yellow", 1.0),
        new(0.50, 0.45, 0.013, "green", 1.0),
        new(0.55, 0.50, 0.014, "blue", 1.0),
        new(0.60, 0.48, 0.012, "white", 1.0),
        new(0.65, 0.45, 0.013, "yellow", 1.0),
        new(0.70, 0.50, 0.015, "pink", 1.0),

        // Lower area
        new(0.20, 0.55, 0.012, "yellow", 1.0),
        new(0.28, 0.58, 0.011, "blue", 1.0),
        new(0.35, 0.55, 0.010, "white", 1.0),
        new(0.42, 0.60, 0.012, "yellow", 1.0),
        new(0.50, 0.57, 0.011, "pink", 1.0),
        new(0.58, 0.55, 0.010, "blue", 1.0),
        new(0.65, 0.60, 0.012, "yellow", 1.0),
        new(0.72, 0.58, 0.011, "white", 1.0),
        new(0.78, 0.55, 0.010, "yellow", 1.0),

        // Bottom footholds — small holds near bottom edge
        new(0.15, 0.68, 0.008, "blue", 0.8),
        new(0.22, 0.70, 0.007, "yellow", 0.8),
        new(0.30, 0.72, 0.008, "white", 0.8),
        new(0.38, 0.68, 0.007, "blue", 0.8),
        new(0.45, 0.71, 0.008, "yellow", 0.8),
        new(0.52, 0.69, 0.007, "white", 0.8),
        new(0.58, 0.72, 0.008, "blue", 0.8),
        new(0.65, 0.70, 0.007, "yellow", 0.8),
        new(0.72, 0.68, 0.008, "white", 0.8),
        new(0.80, 0.71, 0.007, "blue", 0.8),

        // Very bottom — tiny footholds along bottom strip
        new(0.20, 0.78, 0.006, "blue", 0.7),
        new(0.30, 0.80, 0.006, "white", 0.7),
        new(0.40, 0.78, 0.006, "yellow", 0.7),
        new(0.50, 0.80, 0.006, "blue", 0.7),
        new(0.60, 0.78, 0.006, "white", 0.7),
        new(0.70, 0.80, 0.006, "yellow", 0.7),
    ];

    [Fact]
    public void RenderManualDetection()
    {
        Directory.CreateDirectory(OutputDir);

        var imagePath = Path.Combine(WallsDir, "Test-Wall.jpeg");
        Skip.If(!File.Exists(imagePath), "Test-Wall.jpeg not found");

        var imageData = File.ReadAllBytes(imagePath);
        var holds = GetManualHolds();

        var outputPath = Path.Combine(OutputDir, "Test-Wall_manual.png");
        RenderAnnotatedImage(imageData, holds, outputPath);

        var summaryPath = Path.Combine(OutputDir, "Test-Wall_manual_summary.txt");
        File.WriteAllLines(summaryPath, new[]
        {
            $"Manual visual identification",
            $"Total holds identified: {holds.Count}",
            $"Colors: {string.Join(", ", holds.GroupBy(h => h.Color).OrderByDescending(g => g.Count()).Select(g => $"{g.Key}:{g.Count()}"))}",
        });
    }

    private static void RenderAnnotatedImage(byte[] imageData, List<DetectedHold> holds, string outputPath)
    {
        using var original = SKBitmap.Decode(imageData);
        using var surface = SKSurface.Create(new SKImageInfo(original.Width, original.Height));
        var canvas = surface.Canvas;

        canvas.DrawBitmap(original, 0, 0);

        var colorMap = new Dictionary<string, SKColor>
        {
            ["yellow"] = new(255, 220, 0, 120),
            ["white"] = new(255, 255, 255, 120),
            ["pink"] = new(255, 80, 150, 120),
            ["red"] = new(220, 40, 40, 120),
            ["blue"] = new(50, 100, 220, 120),
            ["green"] = new(50, 200, 80, 120),
            ["orange"] = new(255, 150, 30, 120),
            ["purple"] = new(150, 50, 200, 120),
            ["brown"] = new(140, 90, 40, 120),
            ["black"] = new(40, 40, 40, 120),
            ["gray"] = new(140, 140, 140, 120),
        };

        var strokePaint = new SKPaint
        {
            Style = SKPaintStyle.Stroke,
            StrokeWidth = 3,
            IsAntialias = true,
        };

        var textPaint = new SKPaint
        {
            Color = SKColors.White,
            TextSize = 12,
            IsAntialias = true,
        };

        var textBgPaint = new SKPaint
        {
            Color = new SKColor(0, 0, 0, 200),
            Style = SKPaintStyle.Fill,
        };

        for (int i = 0; i < holds.Count; i++)
        {
            var hold = holds[i];
            float cx = (float)(hold.X * original.Width);
            float cy = (float)(hold.Y * original.Height);
            float r = (float)(hold.Radius * Math.Max(original.Width, original.Height));

            var fillColor = colorMap.GetValueOrDefault(hold.Color ?? "white", new SKColor(200, 200, 200, 100));
            var fill = new SKPaint { Color = fillColor, Style = SKPaintStyle.Fill, IsAntialias = true };
            strokePaint.Color = new SKColor(fillColor.Red, fillColor.Green, fillColor.Blue, 255);

            canvas.DrawCircle(cx, cy, r, fill);
            canvas.DrawCircle(cx, cy, r, strokePaint);

            var label = $"#{i + 1} {hold.Color}";
            var textBounds = new SKRect();
            textPaint.MeasureText(label, ref textBounds);
            canvas.DrawRect(cx - 1, cy - r - textBounds.Height - 4, textBounds.Width + 4, textBounds.Height + 2, textBgPaint);
            canvas.DrawText(label, cx + 1, cy - r - 3, textPaint);
        }

        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 95);
        using var stream = File.OpenWrite(outputPath);
        data.SaveTo(stream);
    }
}
