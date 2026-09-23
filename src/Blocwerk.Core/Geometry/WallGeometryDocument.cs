using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocwerk.Core.Geometry;

/// <summary>
/// The measured wall model (<c>wall-geometry.json</c>, v1) produced by the offline glyph solver
/// (<c>tools/glyph/geometry/</c>): per-facet plane frames and every placed marker's corners in its
/// facet's plane, in mm. See <c>tools/glyph/wall-geometry.schema.md</c> for the contract.
/// Deserialization is tolerant: unknown fields, comments and trailing commas are ignored.
/// </summary>
public sealed record WallGeometryDocument
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public int Version { get; init; }

    public string Units { get; init; } = "mm";

    public string Dictionary { get; init; } = "DICT_4X4_50";

    public string? IdScheme { get; init; }

    /// <summary>Printed black-square side in mm.</summary>
    public double MarkerSizeMm { get; init; } = 125.0;

    public WallGeometryWorld? World { get; init; }

    public IReadOnlyList<WallGeometrySegment> Segments { get; init; } = [];

    public IReadOnlyList<WallGeometryMarker> Markers { get; init; } = [];

    public WallGeometryQuality? Quality { get; init; }

    /// <summary>Parses a <c>wall-geometry.json</c> payload.</summary>
    /// <exception cref="JsonException">The JSON is malformed or not an object.</exception>
    public static WallGeometryDocument Parse(string json)
    {
        return JsonSerializer.Deserialize<WallGeometryDocument>(json, JsonOptions)
               ?? throw new JsonException("wall-geometry.json is empty.");
    }

    /// <summary>Parses a <c>wall-geometry.json</c> stream.</summary>
    public static async Task<WallGeometryDocument> LoadAsync(Stream json, CancellationToken ct = default)
    {
        return await JsonSerializer.DeserializeAsync<WallGeometryDocument>(json, JsonOptions, ct)
               ?? throw new JsonException("wall-geometry.json is empty.");
    }

    /// <summary>The marker entry for <paramref name="id"/>, or null when it is not on the wall.</summary>
    public WallGeometryMarker? FindMarker(int id) => Markers.FirstOrDefault(m => m.Id == id);

    /// <summary>The facet with <paramref name="facetId"/> and its segment, or null.</summary>
    public (WallGeometrySegment Segment, WallGeometryFacet Facet)? FindFacet(string facetId)
    {
        foreach (var segment in Segments)
        {
            var facet = segment.Facets.FirstOrDefault(f => f.Id == facetId);
            if (facet is not null)
            {
                return (segment, facet);
            }
        }

        return null;
    }
}
