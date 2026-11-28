// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Services.Membership;

/// <summary>
/// Ending a membership: a wall admin removes a member, or a member leaves. Both end it the same way
/// (<see cref="WallMembershipEnd"/>) and replace the wall's invite token, so a removed member's old link does not let
/// them back in.
/// </summary>
public interface IWallMembershipService
{
    /// <summary>Removes <paramref name="userId"/> from the wall. Wall admins only; never the owner, never oneself.</summary>
    /// <exception cref="UnauthorizedAccessException">The caller is not an admin of the wall.</exception>
    /// <exception cref="MembershipRefusedException">The rules refuse it.</exception>
    Task RemoveMemberAsync(Guid wallId, Guid userId, CancellationToken ct = default);

    /// <summary>The caller leaves the wall. Any member but the owner.</summary>
    /// <exception cref="MembershipRefusedException">The caller is not a member, or owns the wall.</exception>
    Task LeaveAsync(Guid wallId, CancellationToken ct = default);
}
