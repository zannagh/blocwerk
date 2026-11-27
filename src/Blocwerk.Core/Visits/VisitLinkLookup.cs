// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Visits;

/// <summary>A visitor link as its landing page reads it, without using it.</summary>
/// <param name="Status">What the link is now.</param>
/// <param name="LinkId">The row's id, or <see cref="Guid.Empty"/> when there is none.</param>
/// <param name="WallId">The wall it opens, or <see cref="Guid.Empty"/> when there is none.</param>
/// <param name="WallName">The wall's name, for the landing page; empty when there is none.</param>
/// <param name="ExpiresAt">When the link ends.</param>
/// <param name="RedemptionId">The redemption's id once used, else null.</param>
public sealed record VisitLinkLookup(
    VisitLinkStatus Status,
    Guid LinkId,
    Guid WallId,
    string WallName,
    DateTimeOffset ExpiresAt,
    Guid? RedemptionId)
{
    /// <summary>The answer for a token that names no link.</summary>
    public static VisitLinkLookup NotFound { get; } =
        new(VisitLinkStatus.NotFound, Guid.Empty, Guid.Empty, string.Empty, DateTimeOffset.MinValue, null);

    /// <summary>True when <paramref name="grant"/> is this link's own redemption: the browser that used it.</summary>
    public bool IsRedeemedBy(VisitGrant? grant) =>
        Status == VisitLinkStatus.Redeemed
        && grant is not null
        && grant.LinkId == LinkId
        && grant.WallId == WallId
        && RedemptionId is { } redemption
        && grant.RedemptionId == redemption;
}
