// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Blocwerk.Client.Sync;

/// <summary>Reads the sync loop's JSON; anything malformed is null, never an exception.</summary>
internal static class SyncJson
{
    public static T? Read<T>(string? json, JsonTypeInfo<T> type)
        where T : class
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize(json, type);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
