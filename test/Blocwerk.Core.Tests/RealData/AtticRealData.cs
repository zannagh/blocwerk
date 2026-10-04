// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Enums;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.Geometry.View3D;

namespace Blocwerk.Core.Tests.RealData;

/// <summary>
/// The Attic's real data (<c>test/real-data/attic/</c>, shared with the Python wall-geometry tests): the active model with
/// its marker corners and 356 solved cameras, the marker plan revision, and the live holds' geometry (positions, radii and
/// outlines only). No photos, no users, no names; the ids are fixed fakes.
/// </summary>
internal static class AtticRealData
{
    /// <summary>The photo aspect (width / height) the stored hold offsets were traced at (a 4:3 phone photo).</summary>
    public const double PhotoAspect = 4.0 / 3.0;

    private static readonly Lazy<string> ModelText = new(() => File.ReadAllText(Path.Combine(Dir, "model.json")));

    /// <summary>The fixture directory.</summary>
    public static string Dir => Path.Combine(AppContext.BaseDirectory, "RealData", "attic");

    /// <summary>The active model's JSON.</summary>
    public static string ModelJson => ModelText.Value;

    /// <summary>The active model.</summary>
    public static WallGeometryDocument Model => WallGeometryDocument.Parse(ModelJson);

    /// <summary>The marker plan JSON.</summary>
    /// <returns>The parsed plan.</returns>
    public static JsonDocument Plan() => JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "plan.json")));

    /// <summary>Every facet with its frame and extent.</summary>
    /// <returns>The facets of the model.</returns>
    public static List<(WallGeometryFacet Facet, FacetFrame Frame, PlaneRectMm Extent)> Facets() =>
        [.. Model.Segments.SelectMany(s => s.Facets).Select(f => (f, FacetFrame.From(f)!, f.ExtentMm!.Value))];

    /// <summary>The live holds of each panel (centre, then right), as the clean-up reads them.</summary>
    /// <returns>One list of holds per panel.</returns>
    public static List<List<Hold>> HoldPanels()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Dir, "holds.json")));
        var next = 1;
        return [.. doc.RootElement.GetProperty("panels").EnumerateArray().Select(p =>
            p.GetProperty("holds").EnumerateArray().Select(h => ReadHold(h, next++)).ToList())];
    }

    private static Hold ReadHold(JsonElement h, int n) => new()
    {
        Id = new Guid(n, 0, 0, new byte[8]),
        X = h.GetProperty("x").GetDouble(),
        Y = h.GetProperty("y").GetDouble(),
        Radius = h.GetProperty("r").GetDouble(),
        IsAutoDetected = h.GetProperty("auto").GetBoolean(),
        IsVirtual = h.GetProperty("virtual").GetBoolean(),
        OutlineSource = h.GetProperty("src").ValueKind == JsonValueKind.Null ? null : (HoldOutlineSource)h.GetProperty("src").GetInt32(),
        ShapePoints = h.TryGetProperty("shape", out var s) ? Points(s) : null,
        ShapeHoles = h.TryGetProperty("holes", out var holes) ? [.. holes.EnumerateArray().Select(Points)] : null,
    };

    private static List<ShapePoint> Points(JsonElement ring) =>
        [.. ring.EnumerateArray().Select(p => new ShapePoint { Dx = p[0].GetDouble(), Dy = p[1].GetDouble() })];
}
