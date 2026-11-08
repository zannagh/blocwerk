// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocwerk.Recognition.Worker;

/// <summary>The contracts' JSON: camelCase, enums by name, nulls left out of what the worker sends.</summary>
public static class WorkerJson
{
    public static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };
}
