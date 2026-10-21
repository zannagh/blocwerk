// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.Text.Json;
using Blocwerk.Contracts.Feed;

namespace Blocwerk.Web.State.Feed;

/// <summary>Reads the access side of feed events: which ones change whom a circuit subscribes as.</summary>
public static class WallFeedAccess
{
    /// <summary>The new role of an <c>access.revoked</c> (a demotion), or null for a removal.</summary>
    public static string? Role(FeedEnvelope revoked) =>
        revoked.Data is { ValueKind: JsonValueKind.Object } data && data.TryGetProperty("role", out var role)
            && role.ValueKind == JsonValueKind.String
            ? role.GetString()
            : null;

    /// <summary>
    /// True for the events after which <paramref name="userId"/>'s audiences may differ without an access event: a
    /// membership change of that user (a promotion, or a re-add), or a wall update that moved the owner. A wall update
    /// without the flag (a row older than <c>WallPayload</c>) counts as one that may have.
    /// </summary>
    public static bool MayChangeViewer(FeedEnvelope e, Guid userId) =>
        (e.Kind == FeedKinds.MemberChanged && e.EntityId == userId) || (e.Kind == FeedKinds.WallUpdated && OwnerChanged(e));

    /// <summary>
    /// What the batch asks of the subscription, the last access-relevant event winning: a removal followed by a re-add
    /// in the same batch re-reads the viewer instead of leaving.
    /// </summary>
    public static WallFeedAccessAction Decide(IReadOnlyList<FeedEnvelope> events, Guid userId)
    {
        var action = WallFeedAccessAction.None;
        foreach (var e in events)
        {
            if (e.Kind == FeedKinds.AccessRevoked)
            {
                action = Role(e) is { } role ? WallFeedAccessAction.Demote(role) : WallFeedAccessAction.Leave;
            }
            else if (MayChangeViewer(e, userId))
            {
                action = WallFeedAccessAction.Reread;
            }
        }

        return action;
    }

    private static bool OwnerChanged(FeedEnvelope e) =>
        e.Data is not { ValueKind: JsonValueKind.Object } data || !data.TryGetProperty("ownerChanged", out var flag)
        || flag.ValueKind != JsonValueKind.False;
}
