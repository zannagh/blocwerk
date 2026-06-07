using Blocwerk.Core.Abstractions;
using Serilog;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.Enums;
using YoloDotNet.Models;
#pragma warning disable CS0618

namespace Blocwerk.HoldDetection;

public class YoloHoldDetectionService : IHoldDetectionService, IDisposable
{
    private Yolo? _yolo;
    private readonly string _modelPath;

    public YoloHoldDetectionService(string modelPath)
    {
        _modelPath = modelPath;
    }

    public Task<List<DetectedHold>> DetectHoldsAsync(byte[] imageData, HoldDetectionParameters? parameters = null)
    {
        try
        {
            EnsureModelLoaded();

            using var skImage = SKImage.FromEncodedData(imageData);
            if (skImage == null)
            {
                Log.Warning("[Hold Detection] Could not decode image");
                return Task.FromResult(new List<DetectedHold>());
            }

            var results = _yolo!.RunObjectDetection(skImage, confidence: 0.25, iou: 0.45);

            var holds = results
                .Select(r =>
                {
                    double cx = (r.BoundingBox.Left + r.BoundingBox.Width / 2.0) / skImage.Width;
                    double cy = (r.BoundingBox.Top + r.BoundingBox.Height / 2.0) / skImage.Height;
                    return new DetectedHold(
                        X: Math.Round(cx, 4),
                        Y: Math.Round(cy, 4),
                        Radius: Math.Round(Math.Max(r.BoundingBox.Width, r.BoundingBox.Height) / (2.0 * Math.Max(skImage.Width, skImage.Height)), 4),
                        Color: r.Label.Name == "volume" ? "white" : null,
                        Confidence: Math.Round(r.Confidence, 3));
                })
                .Where(h => h.X is >= 0 and <= 1 && h.Y is >= 0 and <= 1)
                .ToList();

            Log.Information("[Hold Detection] YOLO detected {Count} valid holds ({Filtered} filtered out-of-bounds)",
                holds.Count, results.Count - holds.Count);

            return Task.FromResult(holds);
        }
        catch (FileNotFoundException)
        {
            Log.Warning("[Hold Detection] Model not found at {Path}, using color-based detection", _modelPath);
            return Task.FromResult(ColorBasedDetection.Detect(imageData, parameters));
        }
        catch (Exception ex)
        {
            Log.Warning("[Hold Detection] YOLO failed ({Type}: {Message}), falling back to color-based detection", ex.GetType().Name, ex.Message);
            return Task.FromResult(ColorBasedDetection.Detect(imageData, parameters));
        }
    }

    private void EnsureModelLoaded()
    {
        if (_yolo != null)
        {
            return;
        }

        if (!File.Exists(_modelPath))
        {
            throw new FileNotFoundException($"YOLO model not found at {_modelPath}");
        }

        _yolo = new Yolo(new YoloOptions
        {
            OnnxModel = _modelPath,
        });

        Log.Information("[Hold Detection] YOLO model loaded from {Path}", _modelPath);
    }

    public void Dispose()
    {
        _yolo?.Dispose();
    }
}
