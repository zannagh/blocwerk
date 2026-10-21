// <copyright file="BaseKeep.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;

namespace Blocwerk.Web.State;

/// <summary>
/// An editor's kept state together with what the boulder looked like when the edit began (<see cref="Base"/>), so a
/// restore can tell the user when the boulder changed since instead of quietly re-applying an edit over someone else's.
/// The boulder has no UpdatedAt column; the base is the editor's own pristine view of it.
/// </summary>
public sealed record BaseKeep(string State, string Base)
{
    /// <summary>The notice shown when the base no longer matches.</summary>
    public const string ChangedSince = "This boulder changed since your unsaved edit was kept; review it before saving.";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Parses a kept wrapper; null for nothing kept or anything unreadable.</summary>
    public static BaseKeep? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BaseKeep>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
