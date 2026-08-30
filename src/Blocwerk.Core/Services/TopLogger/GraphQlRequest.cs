using System.Text.Json.Serialization;

namespace Blocwerk.Core.Services.TopLogger;

/// <summary>
/// The standard GraphQL-over-HTTP request envelope.
/// </summary>
public sealed record GraphQlRequest(
    [property: JsonPropertyName("query")] string Query,
    [property: JsonPropertyName("variables")] object? Variables = null,
    [property: JsonPropertyName("operationName")] string? OperationName = null);
