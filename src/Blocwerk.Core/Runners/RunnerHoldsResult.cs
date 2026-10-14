// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Abstractions;
using Blocwerk.Core.Detection;
using Blocwerk.Core.Geometry.Proposals;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The result a runner uploads for a hold detection job: one JSON document,
/// <c>{"version":1,"photos":{"&lt;camera&gt;":{"width":W,"height":H,"boxes":[[left,top,right,bottom,confidence,"hold"],…]}}}</c>,
/// the merged boxes of the app's tiled detector per photo (a photo the runner could not read is left out; the app detects
/// that one itself). The server turns the boxes into holds the same way the app's detector does
/// (<see cref="ToDetections"/>: normalised, out-of-image centres dropped, the mat filter, volumes left out).
/// </summary>
public static class RunnerHoldsResult
{
    /// <summary>The <see cref="Entities.GpuJob.ResultFormat"/> of a hold detection result.</summary>
    public const string Format = "json";

    /// <summary>The largest result accepted (a few MB for hundreds of photos).</summary>
    public const long MaxBytes = 64L * 1024 * 1024;

    private const int MaxPhotos = 10_000;
    private const int MaxBoxesPerPhoto = 10_000;
    private const int MaxSidePx = 100_000;
    private const string VolumeLabel = "volume";

    /// <summary>The server's check of an uploaded result; throws <see cref="InvalidDataException"/> when it is not one.</summary>
    /// <param name="path">The stored upload.</param>
    public static void Validate(string path) => _ = Read(path);

    /// <summary>The holds per camera name of a stored result; throws <see cref="InvalidDataException"/> when it is not one.</summary>
    /// <param name="path">The stored result.</param>
    /// <returns>The holds per photo.</returns>
    public static IReadOnlyDictionary<string, RunnerPhotoHolds> Read(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length is 0 or > MaxBytes)
        {
            throw new InvalidDataException("The hold detection result is missing, empty or too large.");
        }

        try
        {
            using var stream = File.OpenRead(path);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            return Parse(doc.RootElement);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The hold detection result is not JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// A photo's boxes as the app's detector reports holds (<c>YoloHoldDetectionService.ToHolds</c> and
    /// <c>InAppCaptureHoldDetector</c>, step for step): centres normalised and rounded, centres outside the photo dropped,
    /// the mat filter applied, then volumes left out and the rest back in the photo's pixels.
    /// </summary>
    /// <param name="photo">The camera name.</param>
    /// <param name="width">The photo's width.</param>
    /// <param name="height">The photo's height.</param>
    /// <param name="boxes">The merged boxes.</param>
    /// <returns>The detections.</returns>
    public static IReadOnlyList<CaptureDetection> ToDetections(string photo, int width, int height, IReadOnlyList<RunnerHoldBox> boxes)
    {
        double longSide = Math.Max(width, height);
        var holds = boxes
            .Select(b => new DetectedHold(
                X: Math.Round((b.Left + ((b.Right - b.Left) / 2.0)) / width, 4),
                Y: Math.Round((b.Top + ((b.Bottom - b.Top) / 2.0)) / height, 4),
                Radius: Math.Round(Math.Max(b.Right - b.Left, b.Bottom - b.Top) / (2.0 * longSide), 4),
                Color: b.Label == VolumeLabel ? "white" : null,
                Confidence: Math.Round(b.Confidence, 3)))
            .Where(h => h.X is >= 0 and <= 1 && h.Y is >= 0 and <= 1)
            .ToList();
        return MatFalseDetectionFilter.Classify(holds).Kept
            .Where(h => h.Color != "white")
            .Select(h => new CaptureDetection(photo, h.X * width, h.Y * height, h.Radius * longSide, h.Confidence))
            .ToList();
    }

    private static Dictionary<string, RunnerPhotoHolds> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1
            || !root.TryGetProperty("photos", out var photos) || photos.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("The hold detection result is not a version 1 document with photos.");
        }

        var result = new Dictionary<string, RunnerPhotoHolds>(StringComparer.OrdinalIgnoreCase);
        foreach (var photo in photos.EnumerateObject())
        {
            if (result.Count >= MaxPhotos || photo.Name.Length is 0 or > 200)
            {
                throw new InvalidDataException("The hold detection result names too many photos or an odd one.");
            }

            var (width, height, boxes) = ParsePhoto(photo.Value);
            result[photo.Name] = new RunnerPhotoHolds(width, height, ToDetections(photo.Name, width, height, boxes));
        }

        return result;
    }

    private static (int Width, int Height, List<RunnerHoldBox> Boxes) ParsePhoto(JsonElement photo)
    {
        if (photo.ValueKind != JsonValueKind.Object
            || !photo.TryGetProperty("width", out var w) || !w.TryGetInt32(out var width) || width is <= 0 or > MaxSidePx
            || !photo.TryGetProperty("height", out var h) || !h.TryGetInt32(out var height) || height is <= 0 or > MaxSidePx
            || !photo.TryGetProperty("boxes", out var boxes) || boxes.ValueKind != JsonValueKind.Array
            || boxes.GetArrayLength() > MaxBoxesPerPhoto)
        {
            throw new InvalidDataException("A photo of the hold detection result has no size or boxes.");
        }

        return (width, height, boxes.EnumerateArray().Select(ParseBox).ToList());
    }

    private static RunnerHoldBox ParseBox(JsonElement box)
    {
        if (box.ValueKind != JsonValueKind.Array || box.GetArrayLength() != 6
            || !box[0].TryGetInt32(out var left) || !box[1].TryGetInt32(out var top)
            || !box[2].TryGetInt32(out var right) || !box[3].TryGetInt32(out var bottom)
            || !box[4].TryGetDouble(out var confidence) || confidence is < 0 or > 1 || double.IsNaN(confidence)
            || box[5].ValueKind != JsonValueKind.String || right < left || bottom < top)
        {
            throw new InvalidDataException("A box of the hold detection result is malformed.");
        }

        var label = box[5].GetString() == VolumeLabel ? VolumeLabel : "hold";
        return new RunnerHoldBox(left, top, right, bottom, confidence, label);
    }
}
