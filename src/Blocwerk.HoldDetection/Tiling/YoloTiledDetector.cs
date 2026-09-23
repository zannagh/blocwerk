using Blocwerk.Core.Configuration;
using SkiaSharp;
using YoloDotNet;
using YoloDotNet.Models;
#pragma warning disable CS0618

namespace Blocwerk.HoldDetection.Tiling;

/// <summary>
/// Runs the single process-wide <see cref="Yolo"/> over overlapping native-resolution windows, one after the
/// other (never in parallel: one ONNX session, and each window already uses every core), plus one whole-image
/// pass that contributes only objects too large for a window's overlap to guarantee uncut (volumes). Zoomed-in
/// windows also fire confidently on plain texture (crash mats, bare panels) in tangles of overlapping boxes;
/// <see cref="TileBoxMerger.DropTangles"/> removes those after the merge, <see cref="TileBoxMerger.DropNested"/>
/// the part detections inside a larger hold.
/// </summary>
internal static class YoloTiledDetector
{
    /// <summary>YoloDotNet's IoU for the per-window NMS, as on the whole-image path.</summary>
    public const double WindowIoU = 0.45;

    /// <summary>Detects on the windows of a decoded raster image; boxes in full-image pixels, merged.</summary>
    /// <param name="yolo">The single YOLO instance.</param>
    /// <param name="options">Its options (sampling is switched per call; the caller serialises).</param>
    /// <param name="image">The decoded raster image.</param>
    /// <param name="settings">Tiling settings.</param>
    /// <returns>The merged boxes and the number of windows run.</returns>
    public static (List<PixelBox> Boxes, int Windows) Detect(Yolo yolo, YoloOptions options, SKImage image, HoldDetectionTilingSettings settings)
    {
        int w = image.Width;
        int h = image.Height;
        var windows = TileGrid.Compute(w, h, settings.TileSize, settings.Overlap);
        var sampling = YoloSampling.Parse(settings.Sampling);
        var all = new List<PixelBox>();

        options.SamplingOptions = sampling;
        foreach (var window in windows)
        {
            using var tile = image.Subset(SKRectI.Create(window.X, window.Y, window.Width, window.Height));
            var local = Run(yolo, tile, settings.Confidence);
            all.AddRange(TileBoxMerger.ToImage(local, window, w, h));
        }

        // Only a whole-image pass sees an object wider than the overlap uncut. Keep just those from it.
        if (windows.Count > 1)
        {
            int minSide = settings.Overlap / 2;
            all.AddRange(Run(yolo, image, settings.Confidence).Where(b => LongSide(b) >= minSide));
        }

        var merged = TileBoxMerger.DropNested(TileBoxMerger.DropTangles(TileBoxMerger.Merge(all)));
        return (merged, windows.Count);
    }

    /// <summary>One YOLO inference on an image, as pixel boxes in that image's coordinates.</summary>
    /// <param name="yolo">The YOLO instance.</param>
    /// <param name="image">The image.</param>
    /// <param name="confidence">Minimum confidence.</param>
    /// <returns>The boxes.</returns>
    public static List<PixelBox> Run(Yolo yolo, SKImage image, double confidence) =>
        yolo.RunObjectDetection(image, confidence: confidence, iou: WindowIoU)
            .Select(r => new PixelBox(
                r.BoundingBox.Left,
                r.BoundingBox.Top,
                r.BoundingBox.Right,
                r.BoundingBox.Bottom,
                r.Confidence,
                r.Label.Name))
            .ToList();

    private static int LongSide(PixelBox box) => Math.Max(box.Width, box.Height);
}
