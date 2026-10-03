// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;

namespace Blocwerk.Core.Runners;

/// <summary>
/// The <c>train.json</c> of a training bundle (the quality profile the runner trains, written by the splat worker's
/// <c>splat-prepare</c>): a small flat JSON object of known keys with number or short string values (or an array of
/// numbers). Anything else refuses the bundle, so nothing but training parameters leaves the server with it.
/// </summary>
public static class RunnerTrainOptions
{
    public const int MaxBytes = 16 * 1024;
    public const int MaxStringLength = 64;
    public const int MaxArrayLength = 64;

    /// <summary>The keys the worker writes (bundle.py <c>PROFILE_KEYS</c> plus <c>version</c>).</summary>
    public static readonly IReadOnlySet<string> AllowedKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "version", "quality", "edge", "frameEdge", "minEdge", "steps", "maxSplats", "minSplats", "growthStop",
        "refineEvery", "growthSelectFraction", "shDegree", "checkpoints",
    };

    /// <summary>Throws <see cref="InvalidDataException"/> unless <paramref name="json"/> is an allowed train.json.</summary>
    public static void Validate(byte[] json)
    {
        if (json.Length > MaxBytes)
        {
            throw new InvalidDataException("The training bundle's train.json is too large.");
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("The training bundle's train.json is not an object.");
            }

            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!AllowedKeys.Contains(property.Name) || !IsAllowedValue(property.Value))
                {
                    throw new InvalidDataException($"The training bundle's train.json has an unexpected entry: {Clip(property.Name)}");
                }
            }
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("The training bundle's train.json is not JSON.", ex);
        }
    }

    private static bool IsAllowedValue(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null => true,
        JsonValueKind.String => value.GetString()!.Length <= MaxStringLength,
        JsonValueKind.Array => value.GetArrayLength() <= MaxArrayLength
                               && value.EnumerateArray().All(e => e.ValueKind == JsonValueKind.Number),
        _ => false,
    };

    private static string Clip(string name) => name.Length <= 40 ? name : name[..40];
}
