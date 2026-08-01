using System.Diagnostics;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Telemetry;
using Serilog;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.Models;
#pragma warning disable CS0618

namespace Blocwerk.HoldDetection;

public sealed class YoloHoldDetectionService : IHoldDetectionService, IDisposable
{
    private readonly string _modelPath;
    private Yolo? _yolo;

    public YoloHoldDetectionService(string modelPath)
    {
        _modelPath = modelPath;
    }

    public Task<List<DetectedHold>> DetectHoldsAsync(byte[] imageData, HoldDetectionParameters? parameters = null)
    {
        var start = Stopwatch.GetTimestamp();
        using var activity = Otel.ActivitySource.StartActivity("HoldDetection.Detect");

        try
        {
            EnsureModelLoaded();

            using var skImage = SKImage.FromEncodedData(imageData);
            if (skImage == null)
            {
                Log.Warning("[Hold Detection] Could not decode image");
                var noneMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                activity?.SetTag("detector", "none");
                activity?.SetTag("holds", 0);
                BlocwerkMetrics.RecordImageRecognition(null, "detect", "none", 0, noneMs);
                return Task.FromResult(new List<DetectedHold>());
            }

            var results = _yolo!.RunObjectDetection(skImage, confidence: 0.25, iou: 0.45);

            var holds = results
                .Select(r =>
                {
                    double cx = (r.BoundingBox.Left + (r.BoundingBox.Width / 2.0)) / skImage.Width;
                    double cy = (r.BoundingBox.Top + (r.BoundingBox.Height / 2.0)) / skImage.Height;
                    return new DetectedHold(
                        X: Math.Round(cx, 4),
                        Y: Math.Round(cy, 4),
                        Radius: Math.Round(Math.Max(r.BoundingBox.Width, r.BoundingBox.Height) / (2.0 * Math.Max(skImage.Width, skImage.Height)), 4),
                        Color: r.Label.Name == "volume" ? "white" : null,
                        Confidence: Math.Round(r.Confidence, 3));
                })
                .Where(h => h.X is >= 0 and <= 1 && h.Y is >= 0 and <= 1)
                .ToList();

            Log.Information(
                "[Hold Detection] YOLO detected {Count} valid holds ({Filtered} filtered out-of-bounds)",
                holds.Count,
                results.Count - holds.Count);

            var successMs = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            activity?.SetTag("detector", "yolo");
            activity?.SetTag("holds", holds.Count);
            BlocwerkMetrics.RecordImageRecognition(null, "detect", "yolo", holds.Count, successMs);
            return Task.FromResult(holds);
        }
        catch (FileNotFoundException)
        {
            Log.Warning("[Hold Detection] Model not found at {Path}, using color-based detection", _modelPath);
            var holds = ColorBasedDetection.Detect(imageData, parameters);
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            activity?.SetTag("detector", "color");
            activity?.SetTag("holds", holds.Count);
            BlocwerkMetrics.RecordImageRecognition(null, "detect", "color", holds.Count, ms);
            return Task.FromResult(holds);
        }
        catch (Exception ex)
        {
            Log.Warning("[Hold Detection] YOLO failed ({Type}: {Message}), falling back to color-based detection", ex.GetType().Name, ex.Message);
            var holds = ColorBasedDetection.Detect(imageData, parameters);
            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            activity?.SetTag("detector", "color");
            activity?.SetTag("holds", holds.Count);
            BlocwerkMetrics.RecordImageRecognition(null, "detect", "color", holds.Count, ms);
            return Task.FromResult(holds);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _yolo?.Dispose();
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
}
