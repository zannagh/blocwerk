// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;

namespace Blocwerk.Core.Geometry.Corrections;

/// <summary>
/// What a correction did to the model version it was derived from, read back from the version's own stamp
/// (<c>quality.correction</c>): the similarity parent → version and the facet it dropped, if any. The stamp carries the
/// exact similarity since it was recorded; older stamps only have the scale, so the rotation and translation are
/// re-derived from a facet frame both documents share (a frame moves rigidly with the world).
/// </summary>
/// <param name="Transform">Parent world → version world.</param>
/// <param name="DroppedFacet">The facet "Not part of the wall" removed, or null.</param>
public sealed record CorrectionEdge(GeometrySimilarity Transform, string? DroppedFacet)
{
    /// <summary>The edge from <paramref name="parentJson"/> to <paramref name="childJson"/>, or null when the child is no correction.</summary>
    /// <param name="childJson">The correction version's document.</param>
    /// <param name="parentJson">The document it was derived from.</param>
    /// <returns>The edge, or null.</returns>
    public static CorrectionEdge? Of(string childJson, string parentJson)
    {
        var child = JsonNode.Parse(childJson)?.AsObject();
        if (child?["quality"]?["correction"] is not JsonObject stamp)
        {
            return null;
        }

        var dropped = GeometryJson.Text(stamp, "kind") == "drop" ? GeometryJson.Text(stamp, "facet") : null;
        var transform = dropped is not null
            ? GeometrySimilarity.Identity
            : Stored(stamp["similarity"]) ?? Derived(JsonNode.Parse(parentJson)?.AsObject(), child, GeometryJson.Number(stamp, "scale") ?? 1);
        return transform is null ? null : new CorrectionEdge(transform, dropped);
    }

    /// <summary>The similarity in its stamp form (full precision).</summary>
    /// <param name="t">The similarity.</param>
    /// <returns>The JSON object.</returns>
    public static JsonObject ToJson(GeometrySimilarity t) => new()
    {
        ["scale"] = t.Scale,
        ["rotation"] = new JsonArray(t.Rotation.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
        ["translation"] = new JsonArray(t.Translation.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()),
    };

    private static GeometrySimilarity? Stored(JsonNode? node) =>
        GeometryJson.Number(node, "scale") is { } s && s > 0
        && GeometryJson.Numbers(node?["rotation"]) is { Length: 9 } q
        && GeometryJson.Vec(node?["translation"]) is { } t
            ? new GeometrySimilarity(s, q, t)
            : null;

    /// <summary><c>Q = F'·Fᵀ</c> with F = [u v n] of a facet in both documents, <c>t = o' − s·Q·o</c>.</summary>
    private static GeometrySimilarity? Derived(JsonObject? parent, JsonObject child, double scale)
    {
        if (parent is null)
        {
            return null;
        }

        var before = GeometryJson.SegmentFacets(parent)
            .GroupBy(x => GeometryJson.Text(x.Facet, "id") ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.First().Facet);
        foreach (var (_, facet) in GeometryJson.SegmentFacets(child))
        {
            if (!before.TryGetValue(GeometryJson.Text(facet, "id") ?? string.Empty, out var old)
                || Frame(old) is not { } f || Frame(facet) is not { } g)
            {
                continue;
            }

            var q = new double[9];
            for (var i = 0; i < 3; i++)
            {
                for (var j = 0; j < 3; j++)
                {
                    q[(3 * i) + j] = (g.U[i] * f.U[j]) + (g.V[i] * f.V[j]) + (g.N[i] * f.N[j]);
                }
            }

            var moved = GeometrySimilarity.Multiply(q, f.O);
            return new GeometrySimilarity(scale, q, [g.O[0] - (scale * moved[0]), g.O[1] - (scale * moved[1]), g.O[2] - (scale * moved[2])]);
        }

        return null;
    }

    private static (double[] O, double[] U, double[] V, double[] N)? Frame(JsonObject facet) =>
        GeometryJson.Vec(facet["origin"]) is { } o && GeometryJson.Vec(facet["u"]) is { } u
        && GeometryJson.Vec(facet["v"]) is { } v
            ? (o, GeometrySimilarity.Unit(u), GeometrySimilarity.Unit(v), GeometrySimilarity.Unit(GeometryJson.Vec(facet["normal"]) ?? GeometrySimilarity.Cross(u, v)))
            : null;
}
