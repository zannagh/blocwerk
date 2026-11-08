// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using Blocwerk.HoldDetection.Tiling;
using Blocwerk.Recognition.Worker.Jobs;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.Models;

namespace Blocwerk.Recognition.Worker.Models;

/// <summary>
/// The app's tiled YOLO detection, box for box (<see cref="YoloTiledDetector"/>), for <c>detect-holds@2</c>. Keeps the one
/// <see cref="Yolo"/> of the process loaded and swaps it only when a job names another model hash. YoloDotNet 3.1 creates
/// its ONNX session itself, so the CPU memory arena cannot be switched off here (design §4: "where YoloDotNet allows it").
/// </summary>
public sealed class YoloBoxDetector : IDisposable
{
    private readonly Lock gate = new();
    private Yolo? yolo;
    private YoloOptions? options;

    public string? LoadedSha256 { get; private set; }

    public void Load(string path, string sha256)
    {
        lock (gate)
        {
            if (LoadedSha256 == sha256)
            {
                return;
            }

            yolo?.Dispose();
            yolo = null;
            LoadedSha256 = null;
            options = new YoloOptions { OnnxModel = path };
            yolo = new Yolo(options);
            LoadedSha256 = sha256;
        }
    }

    /// <summary>Detects on one photo; boxes in its pixels, as <c>[left, top, right, bottom, confidence, label]</c>.</summary>
    public (int Width, int Height, IReadOnlyList<object[]> Boxes) Detect(byte[] photo, DetectRequest request)
    {
        lock (gate)
        {
            if (yolo is null || options is null)
            {
                throw new InvalidOperationException("No model is loaded.");
            }

            using var encoded = SKImage.FromEncodedData(photo) ?? throw new RecognitionInputException("A photo cannot be decoded.");
            List<PixelBox> boxes;
            if (request.Tiling.Enabled)
            {
                using var raster = encoded.ToRasterImage(true);
                boxes = YoloTiledDetector.Detect(yolo, options, raster, request.Tiling).Boxes;
            }
            else
            {
                options.SamplingOptions = YoloSampling.Nearest;
                boxes = YoloTiledDetector.Run(yolo, encoded, request.WholeImageConfidence);
            }

            return (encoded.Width, encoded.Height, boxes.Select(ToWire).ToList());
        }
    }

    public void Dispose()
    {
        yolo?.Dispose();
    }

    internal static object[] ToWire(PixelBox b) => [b.Left, b.Top, b.Right, b.Bottom, Math.Clamp(b.Confidence, 0, 1), b.Label];
}
