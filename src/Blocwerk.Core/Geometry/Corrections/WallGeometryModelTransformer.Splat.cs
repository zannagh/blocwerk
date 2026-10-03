// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;

namespace Blocwerk.Core.Geometry.Corrections;

/// <summary>The photo-real scene's <c>frame.json</c> under a correction: the <c>.spz</c> stays, its transforms move.</summary>
public static partial class WallGeometryModelTransformer
{
    /// <summary>
    /// The splat frame after <paramref name="t"/>: <c>toWorldMm</c> (and the fine alignment's) become <c>S·M</c>, the
    /// scale per unit grows by <c>s</c>, and the viewer transform (<c>toViewer</c>/<c>matrix</c>, metres around the reference
    /// facet's centre) and its <c>crop</c> follow the moved centre. The splat file itself is untouched.
    /// </summary>
    /// <param name="frameJson">The stored <c>frame.json</c>.</param>
    /// <param name="t">The similarity.</param>
    /// <returns>The new frame JSON.</returns>
    public static string TransformSplatFrame(string frameJson, GeometrySimilarity t)
    {
        var root = JsonNode.Parse(frameJson)!.AsObject();
        var s = t.ToMatrix4();
        var world = GeometryJson.Matrix4(root["toWorldMm"]);
        if (world is not null)
        {
            TransformViewer(root, world, t);
            root["toWorldMm"] = GeometryJson.Rows(GeometryJson.Multiply4(s, world), 9);
        }

        if (root["refinement"] is JsonObject refinement && GeometryJson.Matrix4(refinement["toWorldMm"]) is { } refined)
        {
            refinement["toWorldMm"] = GeometryJson.Rows(GeometryJson.Multiply4(s, refined), 9);
        }

        if (GeometryJson.Number(root, "scaleMmPerUnit") is { } perUnit)
        {
            root["scaleMmPerUnit"] = perUnit * t.Scale;
        }

        return root.ToJsonString();
    }

    /// <summary>
    /// <c>toViewer = V·toWorld</c> with <c>V = [W | −W·c]</c> (W: world mm → viewer metres, c: the reference centre). The
    /// new viewer keeps W and centres on <c>S(c)</c>, so the splat sits on the moved facets exactly as before.
    /// </summary>
    private static void TransformViewer(JsonObject root, double[] world, GeometrySimilarity t)
    {
        if (GeometryJson.Matrix4(root["toViewer"]) is not { } viewer || GeometryJson.InverseAffine(world) is not { } inverse)
        {
            return;
        }

        var v = GeometryJson.Multiply4(viewer, inverse);
        double[] w = [v[0], v[1], v[2], v[4], v[5], v[6], v[8], v[9], v[10]];
        if (GeometryJson.Inverse3(w) is not { } wInverse)
        {
            return;
        }

        var tv = new[] { v[3], v[7], v[11] };
        var centre = GeometrySimilarity.Multiply(wInverse, tv).Select(x => -x).ToArray();
        var moved = t.Apply(centre);
        var shift = GeometrySimilarity.Multiply(w, moved);
        double[] next = [w[0], w[1], w[2], -shift[0], w[3], w[4], w[5], -shift[1], w[6], w[7], w[8], -shift[2], 0, 0, 0, 1];
        var toViewer = GeometryJson.Multiply4(next, GeometryJson.Multiply4(t.ToMatrix4(), world));
        root["toViewer"] = GeometryJson.Rows(toViewer, 9);

        // Column-major for three.js Matrix4.fromArray.
        root["matrix"] = GeometryJson.Array(Enumerable.Range(0, 16).Select(i => toViewer[((i % 4) * 4) + (i / 4)]).ToArray(), 9);
        TransformCrop(root, w, wInverse, centre, moved, t);
    }

    /// <summary>The crop box (viewer metres) mapped corner by corner, as the new axis-aligned bounds.</summary>
    private static void TransformCrop(JsonObject root, double[] w, double[] wInverse, double[] centre, double[] moved, GeometrySimilarity t)
    {
        if (root["crop"] is not JsonArray { Count: 2 } crop || GeometryJson.Vec(crop[0]) is not { } lo || GeometryJson.Vec(crop[1]) is not { } hi)
        {
            return;
        }

        var corners = new List<double[]>();
        foreach (var x in new[] { lo[0], hi[0] })
        {
            foreach (var y in new[] { lo[1], hi[1] })
            {
                foreach (var z in new[] { lo[2], hi[2] })
                {
                    var p = GeometrySimilarity.Multiply(wInverse, [x, y, z]);
                    var mapped = t.Apply([centre[0] + p[0], centre[1] + p[1], centre[2] + p[2]]);
                    corners.Add(GeometrySimilarity.Multiply(w, [mapped[0] - moved[0], mapped[1] - moved[1], mapped[2] - moved[2]]));
                }
            }
        }

        root["crop"] = new JsonArray(
            GeometryJson.Array([.. Enumerable.Range(0, 3).Select(i => corners.Min(c => c[i]))], 4),
            GeometryJson.Array([.. Enumerable.Range(0, 3).Select(i => corners.Max(c => c[i]))], 4));
    }
}
