// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

namespace Blocwerk.Core.Visits;

/// <summary>
/// One browser's right to view one wall read-only until <see cref="ExpiresAt"/>, made by redeeming a visitor link and
/// carried in the protected visit cookie.
/// </summary>
/// <param name="LinkId">The link it was made from.</param>
/// <param name="WallId">The one wall it opens.</param>
/// <param name="RedemptionId">The link's redemption id: binds the cookie to the one redemption.</param>
/// <param name="ExpiresAt">The link's expiry; the visit ends then.</param>
public sealed record VisitGrant(Guid LinkId, Guid WallId, Guid RedemptionId, DateTimeOffset ExpiresAt)
{
    /// <summary>True while the visit lasts.</summary>
    public bool IsActiveAt(DateTimeOffset now) => WallId != Guid.Empty && now < ExpiresAt;
}
