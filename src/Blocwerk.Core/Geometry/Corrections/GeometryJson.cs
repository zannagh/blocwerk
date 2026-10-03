// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blocwerk.Core.Geometry.Corrections;

/// <summary>Small JSON-node helpers for rewriting geometry documents and splat frames in place.</summary>
internal static class GeometryJson
{
    /// <summary>Every (segment, facet) object of a document.</summary>
    public static List<(JsonObject Segment, JsonObject Facet)> SegmentFacets(JsonObject root) =>
        (root["segments"] as JsonArray ?? [])
            .OfType<JsonObject>()
            .SelectMany(s => (s["facets"] as JsonArray ?? []).OfType<JsonObject>().Select(f => (s, f)))
            .ToList();

    /// <summary>The string under <paramref name="key"/>, or null.</summary>
    public static string? Text(JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>The number under <paramref name="key"/>, or null.</summary>
    public static double? Number(JsonNode? node, string key) =>
        node?[key] is JsonValue v && v.GetValueKind() == JsonValueKind.Number ? v.GetValue<double>() : null;

    /// <summary>A 3-vector, or null when the node is not one.</summary>
    public static double[]? Vec(JsonNode? node) => Numbers(node) is { Length: 3 } v ? v : null;

    /// <summary>An array of numbers, or null when the node is not one.</summary>
    public static double[]? Numbers(JsonNode? node)
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

    /// <summary>A row-major 4×4 from nested rows (<c>[[..4], ×4]</c>), or null.</summary>
    public static double[]? Matrix4(JsonNode? node)
    {
        if (node is not JsonArray { Count: 4 } rows)
        {
            return null;
        }

        var m = new double[16];
        for (var r = 0; r < 4; r++)
        {
            if (Numbers(rows[r]) is not { Length: 4 } row)
            {
                return null;
            }

            System.Array.Copy(row, 0, m, r * 4, 4);
        }

        return m.All(double.IsFinite) ? m : null;
    }

    /// <summary>Nested rows of a row-major 4×4.</summary>
    public static JsonArray Rows(double[] m, int digits) =>
        new(Enumerable.Range(0, 4).Select(r => (JsonNode?)Array(m[(r * 4)..((r * 4) + 4)], digits)).ToArray());

    /// <summary>Sets a vector when it is not null.</summary>
    public static void Set(JsonObject node, string key, double[]? value, int digits)
    {
        if (value is not null)
        {
            node[key] = Array(value, digits);
        }
    }

    /// <summary>A JSON array of rounded numbers (never "-0").</summary>
    public static JsonArray Array(double[] values, int digits) =>
        new(values.Select(v => (JsonNode?)(Math.Round(v, digits) + 0.0)).ToArray());

    /// <summary>The object under <paramref name="key"/>, created when missing or of another kind.</summary>
    public static JsonObject Child(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing)
        {
            return existing;
        }

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    /// <summary>Row-major 4×4 product.</summary>
    public static double[] Multiply4(double[] a, double[] b)
    {
        var m = new double[16];
        for (var r = 0; r < 4; r++)
        {
            for (var c = 0; c < 4; c++)
            {
                m[(r * 4) + c] = Enumerable.Range(0, 4).Sum(k => a[(r * 4) + k] * b[(k * 4) + c]);
            }
        }

        return m;
    }

    /// <summary>The inverse of an affine row-major 4×4 (last row 0 0 0 1), or null when singular.</summary>
    public static double[]? InverseAffine(double[] m)
    {
        double[] a = [m[0], m[1], m[2], m[4], m[5], m[6], m[8], m[9], m[10]];
        if (Inverse3(a) is not { } inv)
        {
            return null;
        }

        var t = GeometrySimilarity.Multiply(inv, [m[3], m[7], m[11]]);
        return [inv[0], inv[1], inv[2], -t[0], inv[3], inv[4], inv[5], -t[1], inv[6], inv[7], inv[8], -t[2], 0, 0, 0, 1];
    }

    /// <summary>The inverse of a row-major 3×3, or null when singular.</summary>
    public static double[]? Inverse3(double[] a)
    {
        var c00 = (a[4] * a[8]) - (a[5] * a[7]);
        var c01 = (a[5] * a[6]) - (a[3] * a[8]);
        var c02 = (a[3] * a[7]) - (a[4] * a[6]);
        var det = (a[0] * c00) + (a[1] * c01) + (a[2] * c02);
        if (Math.Abs(det) < 1e-18)
        {
            return null;
        }

        var d = 1 / det;
        return
        [
            c00 * d, ((a[2] * a[7]) - (a[1] * a[8])) * d, ((a[1] * a[5]) - (a[2] * a[4])) * d,
            c01 * d, ((a[0] * a[8]) - (a[2] * a[6])) * d, ((a[2] * a[3]) - (a[0] * a[5])) * d,
            c02 * d, ((a[1] * a[6]) - (a[0] * a[7])) * d, ((a[0] * a[4]) - (a[1] * a[3])) * d,
        ];
    }
}
