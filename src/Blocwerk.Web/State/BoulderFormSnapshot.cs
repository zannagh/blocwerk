// <copyright file="BoulderFormSnapshot.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json;

namespace Blocwerk.Web.State;

/// <summary>
/// The boulder create/revise form as its <c>SnapshotJson</c> writes it (the offline queue's contract, see
/// offline-boulder.js), read back when an editor restores state kept on the device across a deploy.
/// </summary>
public sealed record BoulderFormSnapshot(
    Guid Id,
    string? Name,
    string? Grade,
    bool KickboardFootholdsOn,
    bool HandsFollowFeet,
    string? FootColorOnly,
    bool NoMatch,
    List<BoulderFormHold>? Holds,
    List<Guid>? Setters)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Parses a kept snapshot; null for nothing kept or anything unreadable.</summary>
    public static BoulderFormSnapshot? TryParse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<BoulderFormSnapshot>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
