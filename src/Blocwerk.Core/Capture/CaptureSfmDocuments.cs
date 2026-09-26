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

/// <summary>A declared angle the feature solve may use to find "up" (a wall segment's or the wall's own angle).</summary>
/// <param name="Index">The segment index.</param>
/// <param name="Name">Its name.</param>
/// <param name="AngleDeg">The declared overhang, degrees.</param>
public sealed record CaptureAngleHint(int Index, string Name, double AngleDeg);

/// <summary>
/// The <c>solve-sfm</c> request (<c>docker/wall-geometry/README.md</c>, "Solve from features") and the anchor naming of a
/// markerless capture. No device gravity yet (that waits for the owner's privacy decision) and no hold detections.
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
    /// <param name="photoIndexes">The capture's photos (their indexes).</param>
    /// <param name="hints">Declared angles.</param>
    /// <param name="scale">A measured distance, or null.</param>
    /// <param name="anchors">Anchor stem → reference camera image; empty = no anchors.</param>
    /// <param name="referenceJson">The reference model the anchors are known in (required with anchors).</param>
    /// <param name="markerSizeMm">Echoed into the document.</param>
    /// <returns>The request.</returns>
    public static string BuildRequest(
        IEnumerable<int> photoIndexes,
        IReadOnlyList<CaptureAngleHint> hints,
        CaptureScaleReference? scale,
        IReadOnlyDictionary<string, string> anchors,
        string? referenceJson,
        double markerSizeMm)
    {
        var request = new JsonObject
        {
            ["photos"] = new JsonArray(photoIndexes.Select(i => (JsonNode?)new JsonObject { ["name"] = CaptureComputeDocuments.PhotoName(i) }).ToArray()),
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
}
