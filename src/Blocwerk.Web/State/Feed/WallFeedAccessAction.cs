// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Web.State.Feed;

/// <summary>What a delivery asks of a circuit's subscription to its wall (<see cref="WallFeedAccess.Decide"/>).</summary>
/// <param name="Kind">The action.</param>
/// <param name="Role">For <see cref="WallFeedAccessKind.Demote"/>, the new role's name.</param>
public sealed record WallFeedAccessAction(WallFeedAccessKind Kind, string? Role = null)
{
    public static WallFeedAccessAction None { get; } = new(WallFeedAccessKind.None);

    public static WallFeedAccessAction Leave { get; } = new(WallFeedAccessKind.Leave);

    public static WallFeedAccessAction Reread { get; } = new(WallFeedAccessKind.Reread);

    public static WallFeedAccessAction Demote(string role) => new(WallFeedAccessKind.Demote, role);
}
