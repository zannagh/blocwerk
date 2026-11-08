// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Blocwerk.Recognition.Worker.Tests;

/// <summary>The copied <c>blocwerk-contracts</c> fixtures (Fixtures/contracts/SOURCES.md) and a canonical JSON form to compare with.</summary>
internal static class ContractFixtures
{
    public static string Text(string relative) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "contracts", relative));

    public static JsonElement Element(string relative) => JsonDocument.Parse(Text(relative)).RootElement.Clone();

    public static T Read<T>(string relative) => JsonSerializer.Deserialize<T>(Text(relative), WorkerJson.Options)!;

    /// <summary>Keys sorted, null members dropped (the worker leaves them out), numbers as doubles.</summary>
    public static string Canonical(JsonNode? node) => node switch
    {
        null => "null",
        JsonObject o => "{" + string.Join(",", o.Where(p => p.Value is not null).OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => $"\"{p.Key}\":{Canonical(p.Value)}")) + "}",
        JsonArray a => "[" + string.Join(",", a.Select(Canonical)) + "]",
        JsonValue v when v.GetValueKind() == JsonValueKind.Number => v.GetValue<double>().ToString("R", CultureInfo.InvariantCulture),
        _ => node.ToJsonString(),
    };

    public static string Canonical(object value) => Canonical(JsonSerializer.SerializeToNode(value, WorkerJson.Options));

    public static string CanonicalFixture(string relative) => Canonical(JsonNode.Parse(Text(relative)));
}
