// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services.Membership;

/// <summary>
/// Ending a membership: a wall admin removes a member, or a member leaves. Both end it the same way
/// (<see cref="WallMembershipEnd"/>) and replace the wall's invite (join) token, so a removed member's old link does not
/// let them back in; the public share link stays. Both write an activity entry.
/// </summary>
public interface IWallMembershipService
{
    /// <summary>
    /// Removes <paramref name="userId"/> from the wall, with the beta clips they uploaded to it. Wall admins only; never
    /// the owner, never oneself.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">The caller is not (or no longer) an admin of the wall.</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException">The membership changed meanwhile.</exception>
    /// <exception cref="MembershipRefusedException">The rules refuse it.</exception>
    Task RemoveMemberAsync(Guid wallId, Guid userId, CancellationToken ct = default);

    /// <summary>The caller leaves the wall. Any member but the owner.</summary>
    /// <param name="wallId">The wall.</param>
    /// <param name="deleteBetaVideos">Whether the beta clips the caller uploaded to this wall go too.</param>
    /// <param name="ct">Cancels the request.</param>
    /// <exception cref="MembershipRefusedException">The caller is not a member, or owns the wall.</exception>
    /// <exception cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException">The membership changed meanwhile.</exception>
    Task LeaveAsync(Guid wallId, bool deleteBetaVideos, CancellationToken ct = default);
}
