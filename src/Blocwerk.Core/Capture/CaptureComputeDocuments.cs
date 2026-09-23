using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Blocwerk.Core.Entities;
using Blocwerk.Core.Geometry;
using Blocwerk.Core.MarkerPlanning;

namespace Blocwerk.Core.Capture;

/// <summary>One facet texture listed by a finished <c>textures</c> job.</summary>
public sealed record TextureManifestEntry(
    string FacetId, string File, double AMin, double AMax, double BMin, double BMax, int WidthPx, int HeightPx)
{
    /// <summary>The coverage mask's file (<c>maskFile</c>); null from a worker that does not make masks.</summary>
    public string? MaskFile { get; init; }
}

/// <summary>
/// The JSON documents exchanged with the geometry worker (<c>docker/wall-geometry/README.md</c>):
/// building the <c>solve</c> request and reading the <c>textures</c> result.
/// </summary>
public static class CaptureComputeDocuments
{
    /// <summary>Most texture rows one job may produce (a wall has a handful of facets).</summary>
    public const int MaxTextureFacets = 64;

    /// <summary>Largest texture side accepted from the worker.</summary>
    public const int MaxTextureSidePx = 16384;

    /// <summary>The photo's name towards the worker. Safe for its <c>^[A-Za-z0-9_.-]+$</c> rule.</summary>
    public static string PhotoName(int index) => $"p{index:D2}";

    /// <summary>
    /// The solve request of a wall WITHOUT a marker plan (legacy <c>segment*6+role</c> ids, one size).
    /// Kept byte for byte as before the plan existed.
    /// </summary>
    public static string BuildSolveRequest(
        double markerSizeMm, CaptureDeclarations declarations, IEnumerable<WallCapturePhoto> photos) =>
        BuildSolveRequest(WallMarkerLayout.Legacy(markerSizeMm), markerSizeMm, declarations, photos);

    /// <summary>
    /// The solve request. Only photos that saw a marker take part; width/height are the RAW pixel
    /// grid the corners were detected in. With a plan the ids carry no meaning, so the request also
    /// says where each marker sits (<c>markerSegments</c>) and how big each was printed
    /// (<c>markerSizeOverridesMm</c>, relative to <paramref name="markerSizeMm"/>), and only the
    /// plan's markers are sent.
    /// </summary>
    public static string BuildSolveRequest(
        WallMarkerLayout layout, double markerSizeMm, CaptureDeclarations declarations, IEnumerable<WallCapturePhoto> photos)
    {
        var photoNodes = PhotoNodes(layout, photos);

        // Only rows that actually declare something (an angle, or "vertical") are sent. A segment the
        // solver hears about is taken as a real surface and never merged, so an empty row for spare
        // filler markers (e.g. segment-4 sheets reused on the main wall) would split them off into a
        // facet of their own; left out, the solver attaches them to the surface they are coplanar with.
        var declared = declarations.Segments
            .Where(s => s.DeclaredAngleDeg.HasValue || s.VerticalReference)
            .OrderBy(s => s.Index);

        var request = new JsonObject
        {
            ["markerSizeMm"] = markerSizeMm,
            ["dictionary"] = "DICT_4X4_50",
            ["idScheme"] = layout.IdScheme,
        };
        if (layout.IsFromPlan)
        {
            AddPlanFields(request, layout, markerSizeMm);
        }

        request["segments"] = new JsonArray(declared.Select(s => (JsonNode?)new JsonObject
        {
            ["index"] = s.Index,
            ["name"] = s.Name,
            ["declaredAngleDeg"] = s.DeclaredAngleDeg,
            ["verticalReference"] = s.VerticalReference,
        }).ToArray());
        request["levelPairs"] = new JsonArray(declarations.LevelPairs
            .Select(p => (JsonNode?)new JsonArray(p[0], p[1])).ToArray());
        request["photos"] = photoNodes;
        request["options"] = new JsonObject { ["validate"] = false };
        return request.ToJsonString();
    }

    /// <summary>
    /// A photo's stored markers that may go to the solver: all of them without a plan (they were
    /// detected against the legacy ids already), only the plan's ids with one.
    /// </summary>
    public static IReadOnlyList<CaptureMarker> UsableMarkers(WallMarkerLayout layout, string? markersJson)
    {
        var markers = ParseMarkers(markersJson);
        return layout.IsFromPlan ? markers.Where(m => layout.AllowedIds.Contains(m.Id)).ToList() : markers;
    }

    public static IReadOnlyList<CaptureMarker> ParseMarkers(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<CaptureMarker>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The geometry document of a finished solve job (<c>result.geometry</c>), as raw JSON.</summary>
    public static string? GeometryFromSolveResult(JsonElement? result) =>
        result is { ValueKind: JsonValueKind.Object } r && r.TryGetProperty("geometry", out var g)
                                                         && g.ValueKind == JsonValueKind.Object
            ? g.GetRawText()
            : null;

    /// <summary>
    /// The facets of a finished textures job. Accepts both the worker's shape (<c>facet</c> +
    /// nested <c>bounds</c>) and the flat one (<c>facetId</c>, <c>aMin</c>…); skips unusable rows —
    /// a facet id a row cannot hold (or one listed twice), a non-finite number, a non-positive size — and reads at most
    /// <see cref="MaxTextureFacets"/> of them.
    /// </summary>
    public static IReadOnlyList<TextureManifestEntry> ParseTextureResult(JsonElement? result)
    {
        if (result is not { ValueKind: JsonValueKind.Object } r
            || !r.TryGetProperty("facets", out var facets) || facets.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var entries = new List<TextureManifestEntry>();
        foreach (var f in facets.EnumerateArray().Take(MaxTextureFacets))
        {
            if (f.ValueKind == JsonValueKind.Object && Entry(f) is { } entry && entries.All(e => e.FacetId != entry.FacetId))
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static TextureManifestEntry? Entry(JsonElement f)
    {
        var bounds = f.TryGetProperty("bounds", out var b) && b.ValueKind == JsonValueKind.Object ? b : f;
        var id = Text(f, "facet") ?? Text(f, "facetId");
        var file = Text(f, "file");
        if (!WallGeometryValidator.IsValidFacetId(id) || string.IsNullOrEmpty(file) || file.Length > 128)
        {
            return null;
        }

        double?[] box = [Num(bounds, "aMin"), Num(bounds, "aMax"), Num(bounds, "bMin"), Num(bounds, "bMax")];
        var (width, height) = (Num(f, "widthPx"), Num(f, "heightPx"));
        if (box.Any(v => v is null) || width is not (>= 1 and <= MaxTextureSidePx) || height is not (>= 1 and <= MaxTextureSidePx))
        {
            return null;
        }

        var mask = Text(f, "maskFile");
        return new TextureManifestEntry(
            id!, file, box[0]!.Value, box[1]!.Value, box[2]!.Value, box[3]!.Value, (int)width.Value, (int)height.Value)
        {
            MaskFile = string.IsNullOrEmpty(mask) || mask.Length > 128 ? null : mask,
        };
    }

    private static JsonArray PhotoNodes(WallMarkerLayout layout, IEnumerable<WallCapturePhoto> photos)
    {
        var photoNodes = new JsonArray();
        foreach (var photo in photos.OrderBy(p => p.Index))
        {
            var markers = UsableMarkers(layout, photo.MarkersJson);
            if (markers.Count == 0)
            {
                continue;
            }

            photoNodes.Add(new JsonObject
            {
                ["name"] = PhotoName(photo.Index),
                ["width"] = photo.Width,
                ["height"] = photo.Height,
                ["focal35mm"] = photo.Focal35mm,
                ["cameraGroup"] = photo.CameraGroup,
                ["markers"] = new JsonArray(markers.Select(MarkerNode).ToArray<JsonNode?>()),
            });
        }

        return photoNodes;
    }

    private static void AddPlanFields(JsonObject request, WallMarkerLayout layout, double markerSizeMm)
    {
        var overrides = new JsonObject();
        var segments = new JsonObject();
        foreach (var marker in layout.Markers.Values.OrderBy(m => m.Id))
        {
            var key = marker.Id.ToString(CultureInfo.InvariantCulture);
            segments[key] = marker.Segment;
            if (marker.SizeMm is { } size && size != markerSizeMm)
            {
                overrides[key] = size;
            }
        }

        request["markerSizeOverridesMm"] = overrides;
        request["markerSegments"] = segments;
    }

    private static JsonObject MarkerNode(CaptureMarker m) => new()
    {
        ["id"] = m.Id,
        ["corners"] = new JsonArray(m.Corners.Select(c => (JsonNode?)new JsonArray(c[0], c[1])).ToArray()),
        ["refined"] = true,
        ["synthetic"] = new JsonArray(Enumerable.Repeat(m.Synthetic, 4).Select(v => (JsonNode?)v).ToArray()),
    };

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    /// <summary>A finite number (JSON number or numeric string), else null — "NaN"/"Infinity" included.</summary>
    private static double? Num(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v))
        {
            return null;
        }

        double? value = v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => null,
        };
        return value is { } x && double.IsFinite(x) ? x : null;
    }
}
