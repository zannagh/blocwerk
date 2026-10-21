// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Feed;
using Blocwerk.Core.Entities;

namespace Blocwerk.Core.Feed;

/// <summary>Builds the delivered envelope (v1) of a stored row: its cursor, its payload, and what hydration found.</summary>
public static class FeedEnvelopes
{
    public static FeedEnvelope Build(WallChange row, Guid epoch, FeedHydrated hydrated, AuthorVisibility visibility)
    {
        var hasContent = FeedHydration.HasContent(row.Kind);
        FeedContent? content = null;
        var tombstone = hasContent && (row.EntityId is not { } id || !hydrated.Content.TryGetValue(id, out content));
        return new FeedEnvelope
        {
            Cursor = new FeedCursor(epoch, FeedPosition.Of(row)).ToString(),
            WallId = row.WallId,
            BoulderId = row.BoulderId,
            Kind = row.Kind,
            EntityId = row.EntityId,
            At = row.CreatedAt,
            Actor = row.ActorUserId is { } actor ? visibility.Describe(hydrated.Authors.GetValueOrDefault(actor)) : null,
            Data = Parse(row.DataJson),
            Hydrated = content,
            Tombstone = tombstone,
        };
    }

    /// <summary>
    /// The envelope of a row just committed in this process (<see cref="FeedCommit"/>): ids, kind and payload only, with
    /// <see cref="FeedCommit.Cursor"/>. Never delivered to a client.
    /// </summary>
    public static FeedEnvelope Committed(WallChange row) => new()
    {
        Cursor = FeedCommit.Cursor.ToString(),
        WallId = row.WallId,
        BoulderId = row.BoulderId,
        Kind = row.Kind,
        EntityId = row.EntityId,
        At = row.CreatedAt,
        Data = Parse(row.DataJson),
    };

    // A payload that does not parse is delivered without data (the client reloads by id) instead of stalling the wall's
    // reads on one bad row.
    private static JsonElement? Parse(string? json)
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<JsonElement>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
