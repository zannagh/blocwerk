// <copyright file="CaptureSfmDocuments.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blocwerk.Core.Capture;

/// <summary>
/// An optional measured distance for a markerless capture's scale (<see cref="Entities.WallCapture.ScaleReferenceJson"/>):
/// two pixel points on one capture photo (its stored resolution, OpenCV convention) and the millimetres between them.
/// </summary>
/// <param name="PhotoIndex">The capture photo's index.</param>
/// <param name="A">The first point, [x, y] px.</param>
/// <param name="B">The second point, [x, y] px.</param>
/// <param name="Mm">The distance, mm.</param>
public sealed record CaptureScaleReference(int PhotoIndex, double[] A, double[] B, double Mm);

/// <summary>One photo of a <c>solve-sfm</c> request: its index, stored size and the phone's gravity vector.</summary>
/// <param name="Index">The capture photo's index (names it: <c>p01</c>…).</param>
/// <param name="Width">The stored image's width, px (with the height, picks the portrait/landscape axis mapping).</param>
/// <param name="Height">The stored image's height, px.</param>
/// <param name="Gravity">The iPhone accelerometer vector, or null.</param>
public sealed record CaptureSfmPhoto(int Index, int Width, int Height, DeviceGravity? Gravity)
{
    /// <summary>The request facts of a stored photo.</summary>
    /// <param name="photo">The photo.</param>
    /// <returns>Its entry.</returns>
    public static CaptureSfmPhoto From(Entities.WallCapturePhoto photo) => new(
        photo.Index,
        photo.Width,
        photo.Height,
        photo is { DeviceGravityX: { } x, DeviceGravityY: { } y, DeviceGravityZ: { } z } ? new DeviceGravity(x, y, z) : null);
}

/// <summary>A declared angle the feature solve may use to find "up" (a wall segment's or the wall's own angle).</summary>
/// <param name="Index">The segment index.</param>
/// <param name="Name">Its name.</param>
/// <param name="AngleDeg">The declared overhang, degrees.</param>
public sealed record CaptureAngleHint(int Index, string Name, double AngleDeg);

/// <summary>
/// The <c>solve-sfm</c> request (<c>docker/wall-geometry/README.md</c>, "Solve from features") and the anchor naming of a
/// markerless capture: the photos with their stored size, the phone's gravity vector (iPhones) and hold detections (the
/// wall-facet score's hold hits).
/// </summary>
public static class CaptureSfmDocuments
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>The name an anchor photo travels under (<c>a00</c>, <c>a01</c>, …: the splat worker's anchors.py).</summary>
    /// <param name="ordinal">Its position in the selection.</param>
    /// <returns>The stem.</returns>
    public static string AnchorName(int ordinal) => string.Create(CultureInfo.InvariantCulture, $"a{ordinal:D2}");

    /// <summary>The anchor stems mapped to the reference cameras they are.</summary>
    /// <param name="referenceImages">The selected reference camera images, in order.</param>
    /// <returns>Stem → image.</returns>
    public static IReadOnlyDictionary<string, string> AnchorMap(IReadOnlyList<string> referenceImages) =>
        referenceImages.Select((image, i) => (Stem: AnchorName(i), Image: image)).ToDictionary(x => x.Stem, x => x.Image, StringComparer.Ordinal);

    /// <summary>The request JSON.</summary>
    /// <param name="photos">The capture's photos.</param>
    /// <param name="holds">Photo index → hold centres [x, y] px on the stored photo; photos without an entry send none.</param>
    /// <param name="hints">Declared angles.</param>
    /// <param name="scale">A measured distance, or null.</param>
    /// <param name="anchors">Anchor stem → reference camera image; empty = no anchors.</param>
    /// <param name="referenceJson">The reference model the anchors are known in (required with anchors).</param>
    /// <param name="markerSizeMm">Echoed into the document.</param>
    /// <returns>The request.</returns>
    public static string BuildRequest(
        IEnumerable<CaptureSfmPhoto> photos,
        IReadOnlyDictionary<int, IReadOnlyList<double[]>> holds,
        IReadOnlyList<CaptureAngleHint> hints,
        CaptureScaleReference? scale,
        IReadOnlyDictionary<string, string> anchors,
        string? referenceJson,
        double markerSizeMm)
    {
        var request = new JsonObject
        {
            ["photos"] = new JsonArray(photos.Select(p => (JsonNode?)PhotoEntry(p, holds.GetValueOrDefault(p.Index))).ToArray()),
            ["segments"] = new JsonArray(hints.Select(h => (JsonNode?)new JsonObject
            {
                ["index"] = h.Index,
                ["name"] = h.Name.Length <= 128 ? h.Name : h.Name[..128],
                ["declaredAngleDeg"] = Math.Clamp(h.AngleDeg, -90, 90),
            }).ToArray()),
            ["markerSizeMm"] = markerSizeMm,
        };
        if (scale is not null)
        {
            request["measuredDistance"] = new JsonObject
            {
                ["photo"] = CaptureComputeDocuments.PhotoName(scale.PhotoIndex),
                ["a"] = new JsonArray(scale.A[0], scale.A[1]),
                ["b"] = new JsonArray(scale.B[0], scale.B[1]),
                ["mm"] = scale.Mm,
            };
        }

        if (anchors.Count > 0 && referenceJson is not null)
        {
            request["anchors"] = new JsonObject(anchors.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)kv.Value)));
            request["reference"] = JsonNode.Parse(referenceJson);
        }

        return request.ToJsonString();
    }

    /// <summary>The stored scale reference, or null when there is none or it is malformed.</summary>
    /// <param name="json">The capture's <see cref="Entities.WallCapture.ScaleReferenceJson"/>.</param>
    /// <returns>The reference.</returns>
    public static CaptureScaleReference? ParseScale(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var s = JsonSerializer.Deserialize<CaptureScaleReference>(json, WebJson);
            return s is { A.Length: 2, B.Length: 2 } && s.Mm > 0 && s.A.Concat(s.B).All(double.IsFinite) ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Why the anchors were not used (<c>quality.sfm.anchors.reason</c>), or null.</summary>
    /// <param name="documentJson">The solved document.</param>
    /// <returns>The solver's reason.</returns>
    public static string? AnchorRefusal(string documentJson)
    {
        try
        {
            var anchors = JsonNode.Parse(documentJson)?["quality"]?["sfm"]?["anchors"];
            return anchors?["reason"] is JsonValue v && v.TryGetValue<string>(out var reason) ? reason : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonObject PhotoEntry(CaptureSfmPhoto photo, IReadOnlyList<double[]>? holds)
    {
        var entry = new JsonObject { ["name"] = CaptureComputeDocuments.PhotoName(photo.Index) };
        if (photo is { Width: > 0, Height: > 0 })
        {
            entry["imageSize"] = new JsonArray(photo.Width, photo.Height);
        }

        if (photo.Gravity is { } g && double.IsFinite(g.X) && double.IsFinite(g.Y) && double.IsFinite(g.Z))
        {
            entry["deviceGravity"] = new JsonArray(g.X, g.Y, g.Z);
        }

        if (holds is { Count: > 0 })
        {
            entry["holds"] = new JsonArray(holds.Select(h => (JsonNode?)new JsonArray(h[0], h[1])).ToArray());
        }

        return entry;
    }
}
