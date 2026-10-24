// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Documents;
using Blocwerk.Contracts.Feed;

namespace Blocwerk.Client.Store;

/// <summary>
/// Applies one feed envelope (<c>/feed/stream</c>, PR 3.12) to a cached wall, by the plan's sync rules (§1): the
/// comment kinds by id at once, <c>boulder.deleted</c> drops the boulder's comments, a <c>boulder.changed</c> to draft
/// purges an outsider class's comments; every structural kind also refetches the docs index, whose new documents then
/// replace the old wholesale. The viewer's own ratings are not offline (<c>rating.mine</c> is ignored); the resync kinds
/// refetch the marks too, because no wall circuit carries them.
/// </summary>
public static class StoreFeedApplier
{
    private static readonly HashSet<string> MineKinds = new(StringComparer.Ordinal) { FeedKinds.TickMine, FeedKinds.FavoriteChanged };

    private static readonly HashSet<string> ResyncKinds = new(StringComparer.Ordinal)
    {
        FeedStreamEvents.Resync, FeedKinds.WallResync, FeedKinds.TicksResync, FeedKinds.UserResync,
    };

    private static readonly HashSet<string> IgnoredKinds = new(StringComparer.Ordinal) { FeedKinds.RatingMine };

    /// <summary>The wall after the event, and what is left to do.</summary>
    /// <param name="wall">The cached wall the event belongs to.</param>
    /// <param name="envelope">The event.</param>
    /// <returns>The new wall (or the same one) and the follow-up.</returns>
    public static (CachedWall Wall, FeedFollowUp FollowUp) Apply(CachedWall wall, FeedEnvelope envelope)
    {
        if (envelope.V != FeedSchema.EnvelopeVersion)
        {
            return (wall, FeedFollowUp.Resync | FeedFollowUp.RefetchMine);
        }

        var kind = envelope.Kind;
        if (ResyncKinds.Contains(kind))
        {
            return (wall, FeedFollowUp.Resync | FeedFollowUp.RefetchMine);
        }

        if (MineKinds.Contains(kind))
        {
            return (wall, FeedFollowUp.RefetchMine);
        }

        if (IgnoredKinds.Contains(kind) || !FeedKinds.IsKnown(kind))
        {
            return (wall, FeedFollowUp.None);
        }

        return kind switch
        {
            FeedKinds.CommentAdded => (Added(wall, envelope), FeedFollowUp.RefetchIndex),
            FeedKinds.CommentDeleted when envelope.EntityId is { } id => (wall.WithCommentDeleted(id), FeedFollowUp.RefetchIndex),
            FeedKinds.BoulderDeleted when (envelope.EntityId ?? envelope.BoulderId) is { } id => (wall.WithBoulderDeleted(id), FeedFollowUp.RefetchIndex | FeedFollowUp.RefetchMine),
            FeedKinds.BoulderChanged => (Changed(wall, envelope), FeedFollowUp.RefetchIndex | FeedFollowUp.RefetchMine),
            _ => (wall, FeedFollowUp.RefetchIndex),
        };
    }

    // The hydrated comment joins; a row without its content (a tombstone, a compacted row) waits for the index.
    private static CachedWall Added(CachedWall wall, FeedEnvelope envelope)
    {
        if (envelope.Tombstone || envelope.EntityId is not { } id || envelope.BoulderId is not { } boulderId
            || envelope.Actor is not { } author || envelope.Hydrated?.Text is not { } text)
        {
            return wall;
        }

        return wall.WithCommentAdded(new CommentsDocumentComment(id, boulderId, author, text, envelope.Hydrated.CreatedAt ?? envelope.At));
    }

    private static CachedWall Changed(CachedWall wall, FeedEnvelope envelope)
    {
        var boulderId = envelope.EntityId ?? envelope.BoulderId;
        return boulderId is { } id && IsDraft(envelope.Data) ? wall.WithBoulderDrafted(id) : wall;
    }

    private static bool IsDraft(JsonElement? data) =>
        data is { ValueKind: JsonValueKind.Object } payload
        && payload.TryGetProperty("isDraft", out var draft)
        && draft.ValueKind == JsonValueKind.True;
}
