using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// The standard GraphQL-over-HTTP response envelope.
/// </summary>
/// <typeparam name="T">The type the <c>data</c> field deserializes into.</typeparam>
public sealed record GraphQlResponse<T>(
    [property: JsonPropertyName("data")] T? Data,
    [property: JsonPropertyName("errors")] IReadOnlyList<GraphQlError>? Errors)
{
    /// <summary>
    /// Whether the response carried any GraphQL errors.
    /// </summary>
    [JsonIgnore]
    public bool HasErrors => Errors is { Count: > 0 };
}

/// <summary>
/// A single GraphQL error entry as returned in the <c>errors</c> array.
/// </summary>
public sealed record GraphQlError(
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("locations")] IReadOnlyList<GraphQlErrorLocation>? Locations = null,
    [property: JsonPropertyName("extensions")] IReadOnlyDictionary<string, JsonElement>? Extensions = null);

/// <summary>
/// A source location within a GraphQL document referenced by an error.
/// </summary>
public sealed record GraphQlErrorLocation(
    [property: JsonPropertyName("line")] int Line,
    [property: JsonPropertyName("column")] int Column);
