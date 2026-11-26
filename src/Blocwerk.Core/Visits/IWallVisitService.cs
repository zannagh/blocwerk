// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Visits;

/// <summary>
/// One-time visitor links: a member makes one, the first browser to open it may view the wall read-only until it
/// expires 24 hours after it was made.
/// </summary>
public interface IWallVisitService
{
    /// <summary>
    /// Makes a visitor link for <paramref name="wallId"/>. Any member, moderator or admin of the wall, its owner, and a
    /// site admin may; nobody anonymous (a visitor included), and no kiosk session.
    /// </summary>
    /// <exception cref="UnauthorizedAccessException">Nobody is signed in, or the caller may not invite to the wall.</exception>
    /// <exception cref="Services.KioskRestrictedException">The session is a kiosk tablet.</exception>
    /// <exception cref="VisitLinkLimitException">The caller made too many links in the last hour.</exception>
    Task<VisitLinkCreated> CreateAsync(Guid wallId, CancellationToken ct = default);

    /// <summary>Reads what <paramref name="token"/> is, without using it.</summary>
    Task<VisitLinkLookup> LookUpAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Uses <paramref name="token"/>: one conditional update marks an unused, unexpired link as used, so of two
    /// concurrent attempts exactly one wins. Null when the link is unknown, expired, or already used.
    /// </summary>
    Task<VisitGrant?> RedeemAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// True when somebody is signed in who already sees <paramref name="wallId"/> (a member or its owner): such a
    /// person is sent to the wall and the link stays unused.
    /// </summary>
    Task<bool> CallerIsMemberAsync(Guid wallId, CancellationToken ct = default);
}
