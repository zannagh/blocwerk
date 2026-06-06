using Blocwerk.Core.Abstractions;
using Serilog;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.ExecutionProvider.Cpu;
using YoloDotNet.Models;

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

            var results = _yolo!.RunObjectDetection(skImage, confidence: 0.15, iou: 0.4);

            var holds = results.Select(r => new DetectedHold(
                X: Math.Round((r.BoundingBox.Left + r.BoundingBox.Width / 2.0) / skImage.Width, 4),
                Y: Math.Round((r.BoundingBox.Top + r.BoundingBox.Height / 2.0) / skImage.Height, 4),
                Radius: Math.Round(Math.Max(r.BoundingBox.Width, r.BoundingBox.Height) / (2.0 * Math.Max(skImage.Width, skImage.Height)), 4),
                Color: null,
                Confidence: Math.Round(r.Confidence, 3)
            )).ToList();

            if (holds.Count > 0)
            {
                Log.Information("[Hold Detection] YOLO detected {Count} holds", holds.Count);
                return Task.FromResult(holds);
            }

            Log.Information("[Hold Detection] YOLO found nothing, falling back to color-based detection");
            return Task.FromResult(ColorBasedDetection.Detect(imageData, parameters));
        }
        catch (FileNotFoundException)
        {
            Log.Warning("[Hold Detection] Model not found at {Path}, using color-based detection", _modelPath);
            return Task.FromResult(ColorBasedDetection.Detect(imageData, parameters));
        }
        catch (Exception ex)
        {
            Log.Warning("[Hold Detection] YOLO failed ({Message}), falling back to color-based detection", ex.Message);
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
            ExecutionProvider = new CpuExecutionProvider(_modelPath),
        });

        Log.Information("[Hold Detection] YOLO model loaded from {Path}", _modelPath);
    }

    public void Dispose()
    {
        _yolo?.Dispose();
    }
}
