using System.Diagnostics;
using Blocwerk.Core.Abstractions;
using Blocwerk.HoldDetection.Outlines;
using Blocwerk.HoldDetection.Tests;
using OpenCvSharp;
using Xunit.Abstractions;

namespace Blocwerk.HoldDetection.Tests.Outlines;

/// <summary>
/// Opt-in benchmark on a real full-resolution wall photo: set <c>BLOCWERK_OUTLINE_FULLRES</c> to the photo
/// and <c>BLOCWERK_YOLO_MODEL</c> to the ONNX model. Runs the detector, outlines every hold and reports
/// ms/hold and the method mix; with <c>BLOCWERK_OUTLINE_DEMO_DIR</c> it also writes an overlay.
/// </summary>
[Collection(OnnxRuntimeCollection.Name)]
public class HoldOutlineFullResBenchmark(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task OutlineEveryDetectedHold_OnARealPhoto()
    {
        string? photo = Environment.GetEnvironmentVariable("BLOCWERK_OUTLINE_FULLRES");
        string? model = Environment.GetEnvironmentVariable("BLOCWERK_YOLO_MODEL");
        Skip.If(string.IsNullOrEmpty(photo) || string.IsNullOrEmpty(model), "Set BLOCWERK_OUTLINE_FULLRES and BLOCWERK_YOLO_MODEL.");

        byte[] bytes = await File.ReadAllBytesAsync(photo!);
        using var yolo = new YoloHoldDetectionService(model!);
        List<DetectedHold> holds = await yolo.DetectHoldsAsync(bytes);

        var service = new OpenCvHoldOutlineService();
        var sw = Stopwatch.StartNew();
        using IHoldOutlineSession session = service.OpenSession(bytes);
        double decodeMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var results = holds.Select(h => session.Outline(HoldSeed.FromDetected(h))).ToList();
        double perHold = sw.Elapsed.TotalMilliseconds / Math.Max(1, holds.Count);

        string mix = string.Join(", ", results.GroupBy(r => r.Method).Select(g => $"{g.Key}={g.Count()}"));
        output.WriteLine($"PERF {Path.GetFileName(photo)}: {holds.Count} holds, decode {decodeMs:F0} ms, {perHold:F2} ms/hold; {mix}");

        string? dir = Environment.GetEnvironmentVariable("BLOCWERK_OUTLINE_DEMO_DIR");
        if (!string.IsNullOrEmpty(dir))
        {
            WriteOverlay(bytes, holds, results, Path.Combine(dir, "fullres_" + Path.GetFileNameWithoutExtension(photo) + ".jpg"));
        }

        Assert.True(perHold < 25, $"{perHold:F1} ms per hold");
    }

    private static void WriteOverlay(byte[] bytes, List<DetectedHold> holds, List<HoldOutlineResult> results, string path)
    {
        using Mat img = Cv2.ImDecode(bytes, ImreadModes.Color);
        int w = img.Width, h = img.Height;
        for (int i = 0; i < holds.Count; i++)
        {
            var c = new Point((int)(holds[i].X * w), (int)(holds[i].Y * h));
            Cv2.Circle(img, c, (int)(holds[i].Radius * Math.Max(w, h)), new Scalar(255, 0, 255), 2);
            if (results[i].Method != HoldOutlineMethod.CircleFallback)
            {
                var poly = results[i].Polygon.Select(p => new Point((int)(p.X * w), (int)(p.Y * h))).ToArray();
                Cv2.Polylines(img, new[] { poly }, true, new Scalar(0, 255, 0), 4);
            }
        }

        using var small = new Mat();
        Cv2.Resize(img, small, new Size(0, 0), 0.5, 0.5, InterpolationFlags.Area);
        Cv2.ImWrite(path, small, new[] { (int)ImwriteFlags.JpegQuality, 80 });
    }
}
