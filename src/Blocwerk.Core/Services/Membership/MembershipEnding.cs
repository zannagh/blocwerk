// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services.Membership;

/// <summary>One membership to end.</summary>
/// <param name="WallId">The wall.</param>
/// <param name="UserId">The person leaving or removed.</param>
/// <param name="RemovedBy">The admin removing them, or null when they leave.</param>
/// <param name="DeleteBetaVideos">Whether the beta clips they uploaded to this wall go too.</param>
internal sealed record MembershipEnding(Guid WallId, Guid UserId, Guid? RemovedBy, bool DeleteBetaVideos)
{
    /// <summary>The member rows to lock: the person, and the removing admin.</summary>
    public Guid[] LockedUsers => RemovedBy is { } admin ? [UserId, admin] : [UserId];
}
