// <copyright file="ShapeJson.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Services;

/// <summary>
/// The JSON text form of outlines on <see cref="WallUpdateShapeProposal"/> — the same serializer settings
/// the context uses for <see cref="Hold.ShapePoints"/>, so a stored proposal reads back like a hold column.
/// </summary>
public static class ShapeJson
{
    public static string? Write(List<ShapePoint>? shape) =>
        shape is { Count: >= 3 } ? JsonSerializer.Serialize(shape) : null;

    public static string? WriteRings(List<List<ShapePoint>>? rings) =>
        rings is { Count: > 0 } ? JsonSerializer.Serialize(rings) : null;

    public static List<ShapePoint>? Read(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<List<ShapePoint>>(json);

    public static List<List<ShapePoint>>? ReadRings(string? json) =>
        string.IsNullOrEmpty(json) ? null : JsonSerializer.Deserialize<List<List<ShapePoint>>>(json);

    /// <summary>Re-expresses offsets taken around (ax, ay) around (x, y) instead.</summary>
    public static List<ShapePoint> Rebase(IEnumerable<ShapePoint> shape, double ax, double ay, double x, double y) =>
        shape.Select(p => new ShapePoint { Dx = p.Dx + (ax - x), Dy = p.Dy + (ay - y) }).ToList();

    /// <summary>A plausible outline: ≥ 3 finite points, each within half the image of its centre.</summary>
    public static bool IsPlausible(List<ShapePoint>? shape) =>
        shape is { Count: >= 3 and <= 256 }
        && shape.All(p => double.IsFinite(p.Dx) && double.IsFinite(p.Dy) && Math.Abs(p.Dx) <= 0.5 && Math.Abs(p.Dy) <= 0.5);
}
