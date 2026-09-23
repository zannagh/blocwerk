using SkiaSharp;
#pragma warning disable CS0618

namespace Blocwerk.HoldDetection.Tests;

[Collection(OnnxRuntimeCollection.Name)]
public class YoloDirectTest(OnnxRuntimeFixture onnx)
{
    [SkippableFact]
    public void RunYoloDirectly()
    {
        Skip.If(onnx.Yolo is null, "Model not found at models/climbingcrux.onnx");
        var yolo = onnx.Yolo!;

        var imagePath = Path.Combine(AppContext.BaseDirectory, "walls", "Test-Wall.jpeg");
        Skip.If(!File.Exists(imagePath), "Test-Wall.jpeg not found");

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
