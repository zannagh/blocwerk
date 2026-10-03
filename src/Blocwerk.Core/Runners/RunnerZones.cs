// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The optional <c>zones.json</c> of a training bundle: the wall zones the splat worker's gsplat trainer spends its
/// splat budget on (splat-worker <c>zones.py</c> spec plus the COLMAP-to-world transform), so a runner trains a wall
/// exactly as the worker would. Only its known shape passes: the facets' planes and outlines (numbers and a short
/// id), the surroundings box, the transform and the numeric zone parameters. No camera, marker or photo data.
/// </summary>
public static class RunnerZones
{
    public const string FileName = "zones.json";
    public const int MaxBytes = 1024 * 1024;
    public const int MaxFacets = 2000;
    private const int MaxIdLength = 64;
    private const int MaxParams = 64;

    private static readonly IReadOnlySet<string> TopKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "version", "facets", "floorMm", "boxLo", "boxHi", "params", "toWorldMm",
    };

    private static readonly IReadOnlyDictionary<string, int> FacetVectors = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["o"] = 3, ["u"] = 3, ["v"] = 3, ["n"] = 3, ["ext"] = 4,
    };

    /// <summary>Throws <see cref="InvalidDataException"/> unless <paramref name="json"/> is a zones document.</summary>
    public static void Validate(byte[] json)
    {
        if (json.Length > MaxBytes)
        {
            throw Refused("is too large");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !TopKeys.Contains(p.Name)))
            {
                throw Refused("has unexpected entries");
            }

            RequireNumbers(root, "boxLo", 3);
            RequireNumbers(root, "boxHi", 3);
            RequireMatrix(root);
            RequireFacets(root);
            RequireParams(root);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The training bundle's zones.json is not JSON.", ex);
        }
    }

    private static void RequireFacets(JsonElement root)
    {
        if (!root.TryGetProperty("facets", out var facets) || facets.ValueKind != JsonValueKind.Array
            || facets.GetArrayLength() is 0 or > MaxFacets)
        {
            throw Refused("needs 1 to 2000 facets");
        }

        foreach (var facet in facets.EnumerateArray())
        {
            if (facet.ValueKind != JsonValueKind.Object
                || facet.EnumerateObject().Any(p => p.Name != "id" && !FacetVectors.ContainsKey(p.Name)))
            {
                throw Refused("has an unexpected facet entry");
            }

            if (facet.TryGetProperty("id", out var id)
                && (id.ValueKind != JsonValueKind.String || id.GetString()!.Length > MaxIdLength))
            {
                throw Refused("has a bad facet id");
            }

            foreach (var (name, length) in FacetVectors)
            {
                RequireNumbers(facet, name, length);
            }
        }
    }

    private static void RequireMatrix(JsonElement root)
    {
        if (!root.TryGetProperty("toWorldMm", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() != 4
            || rows.EnumerateArray().Any(r => !IsNumbers(r, 4)))
        {
            throw Refused("needs a 4x4 toWorldMm");
        }
    }

    private static void RequireParams(JsonElement root)
    {
        if (!root.TryGetProperty("params", out var p) || p.ValueKind != JsonValueKind.Object
            || p.EnumerateObject().Count() > MaxParams
            || p.EnumerateObject().Any(e => e.Name.Length > MaxIdLength
                                            || e.Value.ValueKind is not (JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)))
        {
            throw Refused("needs numeric params");
        }
    }

    private static void RequireNumbers(JsonElement parent, string name, int length)
    {
        if (!parent.TryGetProperty(name, out var value) || !IsNumbers(value, length))
        {
            throw Refused($"needs {name} as {length} numbers");
        }
    }

    private static bool IsNumbers(JsonElement value, int length) =>
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == length
                                                && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Number);

    private static InvalidDataException Refused(string why) => new($"The training bundle's zones.json {why}.");
}
