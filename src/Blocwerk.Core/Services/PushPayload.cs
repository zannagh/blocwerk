using System.Text.Json.Serialization;

namespace Blocwerk.Core.Services;

/// <summary>
/// The message body delivered to the service worker's <c>push</c> handler. The field names are
/// serialized lowercase and MUST stay in sync with what <c>service-worker.js</c> reads:
/// <c>title</c>, <c>body</c>, <c>url</c>, <c>tag</c>, <c>icon</c>.
/// </summary>
public sealed record PushPayload(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("body")] string Body,
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("icon")] string? Icon = null);
