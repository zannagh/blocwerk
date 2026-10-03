using System.Diagnostics;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Configuration;
using Blocwerk.Core.Detection;
using Blocwerk.Core.Telemetry;
using Blocwerk.HoldDetection.Tiling;
using Serilog;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.Models;

namespace Blocwerk.HoldDetection;

/// <summary>
/// YOLO hold detection. By default the photo is detected in overlapping native-resolution windows
/// (<see cref="HoldDetectionTilingSettings"/>); with tiling off it is letterboxed whole into the model's
/// 640 px input as before. Either way the boxes become normalised <see cref="DetectedHold"/>s and pass the
/// mat/floor filter. Only ONE <see cref="Yolo"/> may exist per process, so every call is serialised.
/// </summary>
public sealed class YoloHoldDetectionService : IHoldDetectionService, IDisposable
{
    private const double WholeImageConfidence = 0.25;

    private readonly string _modelPath;
    private readonly HoldDetectionTilingSettings tiling;
    private readonly Lock gate = new();
    private readonly bool ownsYolo = true;
    private Yolo? _yolo;
    private YoloOptions? options;

    public YoloHoldDetectionService(string modelPath, HoldDetectionTilingSettings? tiling = null)
    {
        _modelPath = modelPath;
        this.tiling = tiling ?? new HoldDetectionTilingSettings();
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="YoloHoldDetectionService"/> class around an existing YOLO
    /// instance (tests: only one may exist per process). The caller keeps ownership.
    /// </summary>
    /// <param name="yolo">The shared instance.</param>
    /// <param name="options">The options it was created with (YoloDotNet keeps reading them).</param>
    /// <param name="tiling">Tiling settings.</param>
    internal YoloHoldDetectionService(Yolo yolo, YoloOptions options, HoldDetectionTilingSettings tiling)
    {
        _modelPath = options.OnnxModel;
        _yolo = yolo;
        this.options = options;
        this.tiling = tiling;
        ownsYolo = false;
    }

    public Task<List<DetectedHold>> DetectHoldsAsync(byte[] imageData, HoldDetectionParameters? parameters = null)
    {
        var start = Stopwatch.GetTimestamp();
        using var activity = Otel.ActivitySource.StartActivity("HoldDetection.Detect");

        try
        {
            List<DetectedHold>? holds;
            lock (gate)
            {
                EnsureModelLoaded();
                holds = DetectWithYolo(imageData, activity);
            }

            var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
            string detector = holds is null ? "none" : "yolo";
            holds ??= [];
            activity?.SetTag("detector", detector);
            activity?.SetTag("holds", holds.Count);
            BlocwerkMetrics.RecordImageRecognition(null, "detect", detector, holds.Count, ms);
            return Task.FromResult(holds);
        }
        catch (FileNotFoundException)
        {
            Log.Warning("[Hold Detection] Model not found at {Path}, using color-based detection", _modelPath);
            return Task.FromResult(DetectByColor(imageData, parameters, start, activity));
        }
        catch (Exception ex)
        {
            Log.Warning("[Hold Detection] YOLO failed ({Type}: {Message}), falling back to color-based detection", ex.GetType().Name, ex.Message);
            return Task.FromResult(DetectByColor(imageData, parameters, start, activity));
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (ownsYolo)
        {
            _yolo?.Dispose();
        }
    }

    /// <summary>Normalises full-image pixel boxes into holds, drops out-of-bounds centres, applies the mat filter.</summary>
    /// <param name="boxes">Boxes in full-image pixels.</param>
    /// <param name="width">Image width.</param>
    /// <param name="height">Image height.</param>
    /// <returns>The kept holds, the out-of-bounds count and the mat-filter drop count.</returns>
    internal static (List<DetectedHold> Holds, int OutOfBounds, int Mats) ToHolds(IReadOnlyCollection<PixelBox> boxes, int width, int height)
    {
        double longSide = Math.Max(width, height);
        var holds = boxes
            .Select(b => new DetectedHold(
                X: Math.Round(b.CenterX / width, 4),
                Y: Math.Round(b.CenterY / height, 4),
                Radius: Math.Round(Math.Max(b.Width, b.Height) / (2.0 * longSide), 4),
                Color: b.Label == "volume" ? "white" : null,
                Confidence: Math.Round(b.Confidence, 3)))
            .Where(h => h.X is >= 0 and <= 1 && h.Y is >= 0 and <= 1)
            .ToList();

        int outOfBounds = boxes.Count - holds.Count;
        var matFiltered = MatFalseDetectionFilter.Classify(holds);
        return (matFiltered.Kept.ToList(), outOfBounds, matFiltered.Dropped.Count);
    }

    /// <summary>Runs YOLO on the photo; null when it cannot be decoded.</summary>
    private List<DetectedHold>? DetectWithYolo(byte[] imageData, Activity? activity)
    {
        using var encoded = SKImage.FromEncodedData(imageData);
        if (encoded == null)
        {
            Log.Warning("[Hold Detection] Could not decode image");
            return null;
        }

        List<PixelBox> boxes;
        int windows = 1;
        if (tiling.Enabled)
        {
            // Decode once: every window is a subset of the same raster, not a fresh JPEG decode.
            using var raster = encoded.ToRasterImage(true);
            (boxes, windows) = YoloTiledDetector.Detect(_yolo!, options!, raster, tiling);
        }
        else
        {
            options!.SamplingOptions = YoloSampling.Nearest;
            boxes = YoloTiledDetector.Run(_yolo!, encoded, WholeImageConfidence);
        }

        var (holds, outOfBounds, mats) = ToHolds(boxes, encoded.Width, encoded.Height);
        activity?.SetTag("windows", windows);
        Log.Information(
            "[Hold Detection] YOLO detected {Count} valid holds over {Windows} window(s) ({Filtered} filtered out-of-bounds, {Mats} rejected as mat/floor false-positives)",
            holds.Count,
            windows,
            outOfBounds,
            mats);
        return holds;
    }

    private static List<DetectedHold> DetectByColor(byte[] imageData, HoldDetectionParameters? parameters, long start, Activity? activity)
    {
        var holds = ColorBasedDetection.Detect(imageData, parameters);
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        activity?.SetTag("detector", "color");
        activity?.SetTag("holds", holds.Count);
        BlocwerkMetrics.RecordImageRecognition(null, "detect", "color", holds.Count, ms);
        return holds;
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

        // The options object stays referenced by YoloDotNet, which reads SamplingOptions on every run.
        options = new YoloOptions { OnnxModel = _modelPath };
        _yolo = new Yolo(options);

        Log.Information("[Hold Detection] YOLO model loaded from {Path}", _modelPath);
    }
}
