// <copyright file="WallFrameRegistrationWriter.Json.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blocwerk.Core.Geometry.Registration;

/// <summary>Small JSON-node helpers for the rewrite.</summary>
public static partial class WallFrameRegistrationWriter
{
    private static IEnumerable<JsonObject> Facets(JsonObject root) => SegmentFacets(root).Select(sf => sf.Facet);

    private static List<(int Segment, JsonObject Facet)> SegmentFacets(JsonObject root) =>
        (root["segments"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .SelectMany(s => (s["facets"] as JsonArray ?? []).OfType<JsonObject>().Select(f => (s["index"]?.GetValue<int>() ?? 0, f)))
            .ToList();

    /// <summary>The object under <paramref name="key"/>, created (or replaced, when of another kind) as needed.</summary>
    private static JsonObject ChildObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    /// <summary>The array under <paramref name="key"/>, created (or replaced, when of another kind) as needed.</summary>
    private static JsonArray ChildArray(JsonObject parent, string key)
    {
        if (parent[key] is JsonArray existing)
        {
            return existing;
        }

        var created = new JsonArray();
        parent[key] = created;
        return created;
    }

    private static double[]? Vec(JsonNode? node) => Numbers(node) is { Length: 3 } v ? v : null;

    private static double[]? Numbers(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            return null;
        }

        var values = new double[array.Count];
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonValue value || value.GetValueKind() != JsonValueKind.Number)
            {
                return null;
            }

            values[i] = value.GetValue<double>();
        }

        return values;
    }

    private static void Set(JsonObject node, string key, double[]? value, int digits)
    {
        if (value is not null)
        {
            node[key] = Array(value, digits);
        }
    }

    private static JsonArray Array(double[] values, int digits) =>
        new(values.Select(v => (JsonNode?)(Math.Round(v, digits) + 0.0)).ToArray());

    private static JsonArray Ints(IEnumerable<int> values) => new(values.Select(v => (JsonNode?)v).ToArray());
}
