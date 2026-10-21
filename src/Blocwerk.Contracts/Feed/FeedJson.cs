// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Feed.Payloads;

namespace Blocwerk.Contracts.Feed;

/// <summary>The one JSON shape of feed payloads and envelopes: web defaults (camelCase), nulls omitted.</summary>
public static class FeedJson
{
    /// <summary>Gets the serializer options for everything in the feed.</summary>
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes a payload by its runtime type, so a derived record keeps its own properties.</summary>
    public static string Serialize(FeedPayload payload) => JsonSerializer.Serialize(payload, payload.GetType(), Options);
}
