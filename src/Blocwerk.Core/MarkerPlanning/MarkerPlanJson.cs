// <copyright file="MarkerPlanJson.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Blocwerk.Core.MarkerPlanning;

/// <summary>
/// The versioned JSON form of a <see cref="MarkerPlan"/> (see <c>tools/glyph/marker-plan.schema.md</c>):
/// camelCase, enums as camelCase strings, a leading <c>"format": "blocwerk-marker-plan"</c> tag so a
/// photo dump's plan is recognisable. Reading ignores unknown fields, comments and trailing commas,
/// but is strict about numbers (no strings, no NaN/Infinity, sane ranges) and refuses anything over
/// <see cref="MaxBytes"/>.
/// </summary>
public static partial class MarkerPlanJson
{
    /// <summary>The value of the top-level <c>format</c> tag.</summary>
    public const string FormatTag = "blocwerk-marker-plan";

    /// <summary>Largest accepted plan JSON, in bytes (UTF-8).</summary>
    public const int MaxBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        NumberHandling = JsonNumberHandling.Strict,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 16,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    /// <summary>Serializes the plan (indented, format tag first).</summary>
    public static string ToJson(MarkerPlan plan)
    {
        var body = JsonSerializer.SerializeToNode(plan, Options)!.AsObject();
        var tagged = new JsonObject { ["format"] = FormatTag };
        foreach (var (key, value) in body.ToList())
        {
            body.Remove(key);
            tagged[key] = value;
        }

        return tagged.ToJsonString(Options);
    }

    /// <summary>Parses and checks plan JSON; null plus readable errors when it is not a usable plan.</summary>
    public static MarkerPlan? FromJson(string? json, out IReadOnlyList<string> errors)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            errors = ["The marker plan is empty."];
            return null;
        }

        if (json.Length > MaxBytes || System.Text.Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            errors = [$"The marker plan is larger than {MaxBytes / 1024 / 1024} MB — is it really a marker plan?"];
            return null;
        }

        var plan = Deserialize(json, out var parseError);
        if (parseError is not null)
        {
            errors = [parseError];
            return null;
        }

        var problems = plan is null ? ["The marker plan is empty."] : CheckShape(plan);
        errors = problems;
        return problems.Count == 0 ? plan : null;
    }

    /// <summary>Parses the JSON into a plan; on failure returns null with one readable message.</summary>
    private static MarkerPlan? Deserialize(string json, out string? error)
    {
        error = null;
        try
        {
            var node = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
                MaxDepth = 16,
            });
            if (node is not JsonObject obj)
            {
                error = "The marker plan must be a JSON object.";
                return null;
            }

            if (obj["format"] is { } format && format.GetValueKind() == JsonValueKind.String
                && format.GetValue<string>() != FormatTag)
            {
                error = $"This JSON is a \"{format.GetValue<string>()}\", not a marker plan.";
                return null;
            }

            return obj.Deserialize<MarkerPlan>(Options);
        }
        catch (JsonException ex)
        {
            var where = ex.Path is { Length: > 1 } path ? $" at {path}" : ex.LineNumber is { } line ? $" near line {line + 1}" : string.Empty;
            error = $"The marker plan is not valid JSON{where}: {FirstSentence(ex.Message)}";
            return null;
        }
        catch (InvalidOperationException ex)
        {
            error = $"The marker plan could not be read: {FirstSentence(ex.Message)}";
            return null;
        }
    }

    private static string FirstSentence(string message)
    {
        var cut = message.IndexOf(". ", StringComparison.Ordinal);
        return cut > 0 ? message[..(cut + 1)] : message;
    }
}
