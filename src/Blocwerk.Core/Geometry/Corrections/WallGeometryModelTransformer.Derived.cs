// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json.Nodes;
using Blocwerk.Core.Detection.Enrichment;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Geometry.Corrections;

/// <summary>
/// The data derived ON a model, mapped by the same similarity as the model itself, so a corrected version starts with
/// exactly its parent's placements. A facet frame moves rigidly with the world, so every facet-plane coordinate, every
/// length and every height above a facet scales by <c>s</c> (areas by <c>s²</c>); directions in facet coordinates
/// (a volume's surface normal) and photo pixels stay. Only world points (a hold proposal's) take the full similarity.
/// </summary>
public static partial class WallGeometryModelTransformer
{
    /// <summary>
    /// Maps a hold's placement (facet position, sizes, fingerprint sizes), footprint, protrusion and volume placement.
    /// Everything it does not know is kept.
    /// </summary>
    /// <param name="hold">The hold (changed in place).</param>
    /// <param name="t">The similarity.</param>
    /// <param name="volumeIds">The volume a placement's volume becomes, or null when it has no counterpart (then it is kept as it is).</param>
    public static void TransformHold(Hold hold, GeometrySimilarity t, Func<Guid, Guid?> volumeIds)
    {
        var s = t.Scale;
        (hold.PlaneAMm, hold.PlaneBMm) = (hold.PlaneAMm * s, hold.PlaneBMm * s);
        (hold.WidthMm, hold.HeightMm, hold.AreaMm2) = (hold.WidthMm * s, hold.HeightMm * s, hold.AreaMm2 * s * s);
        hold.FingerprintJson = ScaleFields(hold.FingerprintJson, [("widthMm", s), ("heightMm", s), ("areaMm2", s * s)]);
        hold.FootprintMm = ScaleFields(hold.FootprintMm, [("heightMm", s), ("shiftA", s), ("shiftB", s)], "outline", s);
        hold.ProtrusionMm = ScaleFields(
            hold.ProtrusionMm, [("baseMm", s), ("heightMm", s), ("apexA", s), ("apexB", s), ("apexMm", s), ("shiftA", s), ("shiftB", s)]);
        hold.VolumePlacementJson = TransformVolumePlacement(hold.VolumePlacementJson, t, volumeIds);
    }

    /// <summary>A hold on a dropped facet: not measured any more (like a placement the evidence contradicts).</summary>
    /// <param name="hold">The hold (changed in place).</param>
    public static void ClearHold(Hold hold)
    {
        (hold.FacetId, hold.PlaneAMm, hold.PlaneBMm, hold.MetricSource) = (null, null, null, HoldMetric.TextureRegistrationRejected);
        (hold.WidthMm, hold.HeightMm, hold.AreaMm2) = (null, null, null);
        (hold.FootprintMm, hold.ProtrusionMm, hold.VolumePlacementJson) = (null, null, null);
    }

    /// <summary>A hold's volume placement mapped (surface point, height, the flat position it came from) onto its volume's counterpart.</summary>
    /// <param name="json">The stored placement, or null.</param>
    /// <param name="t">The similarity.</param>
    /// <param name="volumeIds">The counterpart of a volume, or null when there is none (the placement is then kept as it is).</param>
    /// <returns>The mapped placement.</returns>
    public static string? TransformVolumePlacement(string? json, GeometrySimilarity t, Func<Guid, Guid?> volumeIds)
    {
        if (Parse(json) is not { } node || !Guid.TryParse(GeometryJson.Text(node, "volumeId"), out var id) || volumeIds(id) is not { } mapped)
        {
            return json;
        }

        node["volumeId"] = mapped.ToString();
        return ScaleFields(node, [("a", t.Scale), ("b", t.Scale), ("h", t.Scale), ("fromA", t.Scale), ("fromB", t.Scale)]);
    }

    /// <summary>A copy of a volume for the mapped model: outline, surface grid and heights, area and height scaled.</summary>
    /// <param name="volume">The volume.</param>
    /// <param name="modelId">The model the copy belongs to.</param>
    /// <param name="t">The similarity.</param>
    /// <returns>The copy (a new id).</returns>
    public static WallVolume TransformVolume(WallVolume volume, Guid modelId, GeometrySimilarity t) => new()
    {
        WallId = volume.WallId,
        GeometryModelId = modelId,
        FacetId = volume.FacetId,
        Index = volume.Index,
        FootprintJson = ScaleArray(volume.FootprintJson, t.Scale),
        SurfaceJson = TransformSurface(volume.SurfaceJson, t.Scale),
        AreaM2 = volume.AreaM2 * t.Scale * t.Scale,
        HeightMm = volume.HeightMm * t.Scale,
        Confidence = volume.Confidence,
        HoldCount = volume.HoldCount,
        Source = volume.Source,
        IsHidden = volume.IsHidden,
        IsRemoved = volume.IsRemoved,
        RemovedAt = volume.RemovedAt,
        HasFlatSides = volume.HasFlatSides,
        HeightFieldJson = volume.HeightFieldJson is null ? null : TransformSurface(volume.HeightFieldJson, t.Scale),
        FlatFitRmsMm = volume.FlatFitRmsMm * t.Scale,
        CreatedAt = volume.CreatedAt,
    };

    /// <summary>A hold proposal mapped: its facet point and size scaled, its world point by the full similarity.</summary>
    /// <param name="proposal">The proposal (changed in place).</param>
    /// <param name="t">The similarity.</param>
    public static void TransformProposal(HoldProposal proposal, GeometrySimilarity t)
    {
        (proposal.A, proposal.B, proposal.H, proposal.SizeMm) = (proposal.A * t.Scale, proposal.B * t.Scale, proposal.H * t.Scale, proposal.SizeMm * t.Scale);
        var world = t.Apply([proposal.X, proposal.Y, proposal.Z]);
        (proposal.X, proposal.Y, proposal.Z) = (world[0], world[1], world[2]);
    }

    /// <summary>A volume surface (height field over its facet): grid origin and cell scaled, int16 heights scaled.</summary>
    internal static string TransformSurface(string json, double s)
    {
        if (Parse(json) is not { } node || GeometryJson.Text(node, "heights") is not { } text)
        {
            return json;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(text);
        }
        catch (FormatException)
        {
            return json;
        }

        for (var k = 0; k + 1 < bytes.Length; k += 2)
        {
            var h = (short)(bytes[k] | (bytes[k + 1] << 8));
            var scaled = (short)Math.Clamp(Math.Round(h * s), short.MinValue, short.MaxValue);
            (bytes[k], bytes[k + 1]) = ((byte)(scaled & 0xff), (byte)((scaled >> 8) & 0xff));
        }

        node["heights"] = Convert.ToBase64String(bytes);
        if (node["faces"] is JsonArray faces)
        {
            // A flat-sided volume's faces: every corner (a, b, height) scales.
            node["faces"] = new JsonArray(faces.Select(f => f is JsonArray ? JsonNode.Parse(ScaleArray(f.ToJsonString(), s)) : f?.DeepClone()).ToArray());
        }

        return ScaleFields(node, [("aLo", s), ("bLo", s), ("cellMm", s)]);
    }

    /// <summary>Scales the named numbers of a JSON object (and every number in its <paramref name="arrayKey"/> array of arrays).</summary>
    private static string? ScaleFields(string? json, (string Key, double Factor)[] fields, string? arrayKey = null, double arrayFactor = 1)
    {
        if (Parse(json) is not { } node)
        {
            return json;
        }

        if (arrayKey is not null && node[arrayKey] is JsonArray rows)
        {
            node[arrayKey] = JsonNode.Parse(ScaleArray(rows.ToJsonString(), arrayFactor));
        }

        return ScaleFields(node, fields);
    }

    private static string ScaleFields(JsonObject node, (string Key, double Factor)[] fields)
    {
        foreach (var (key, factor) in fields)
        {
            if (GeometryJson.Number(node, key) is { } value)
            {
                node[key] = value * factor;
            }
        }

        return node.ToJsonString();
    }

    /// <summary>Every number of a JSON array of number arrays (<c>[[a, b], …]</c>) scaled.</summary>
    private static string ScaleArray(string json, double s)
    {
        if (JsonNode.Parse(json) is not JsonArray rows)
        {
            return json;
        }

        var scaled = rows.Select(r => GeometryJson.Numbers(r) is { } v ? (JsonNode?)new JsonArray(v.Select(x => (JsonNode?)JsonValue.Create(x * s)).ToArray()) : r?.DeepClone());
        return new JsonArray(scaled.ToArray()).ToJsonString();
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }
}
