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
    /// membership change of that user (a promotion), or a wall update (the owner may have changed).
    /// </summary>
    public static bool MayChangeViewer(FeedEnvelope e, Guid userId) =>
        (e.Kind == FeedKinds.MemberChanged && e.EntityId == userId) || e.Kind == FeedKinds.WallUpdated;
}
