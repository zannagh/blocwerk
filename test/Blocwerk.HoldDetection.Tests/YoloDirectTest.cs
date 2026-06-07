using SkiaSharp;
using YoloDotNet;
using YoloDotNet.Models;
#pragma warning disable CS0618

namespace Blocwerk.HoldDetection.Tests;

public class YoloDirectTest
{
    [SkippableFact]
    public void RunYoloDirectly()
    {
        var modelPath = HoldDetectionServices.ResolveModelPath("models/climbingcrux.onnx");
        Skip.If(!File.Exists(modelPath), $"Model not found at {modelPath}");

        var imagePath = Path.Combine(AppContext.BaseDirectory, "walls", "Test-Wall.jpeg");
        Skip.If(!File.Exists(imagePath), "Test-Wall.jpeg not found");

        using var yolo = new Yolo(new YoloOptions
        {
            OnnxModel = modelPath,
        });

        var imageData = File.ReadAllBytes(imagePath);
        using var skImage = SKImage.FromEncodedData(imageData);

        var results = yolo.RunObjectDetection(skImage, confidence: 0.1, iou: 0.5);

        var outputDir = Path.Combine(AppContext.BaseDirectory, "walls", "output");
        Directory.CreateDirectory(outputDir);
        File.WriteAllText(Path.Combine(outputDir, "yolo_direct_results.txt"),
            $"Results: {results.Count}\n" +
            string.Join("\n", results.Select(r =>
                $"Label={r.Label.Name} Conf={r.Confidence:P1} Box=({r.BoundingBox.Left},{r.BoundingBox.Top},{r.BoundingBox.Width},{r.BoundingBox.Height})")));

        Assert.True(results.Count > 0, $"YOLO returned 0 results. Model may be incompatible.");
    }
}
