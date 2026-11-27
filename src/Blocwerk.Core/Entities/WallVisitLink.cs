// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A one-time visitor link for one wall: whoever opens it first may browse that wall read-only, without an account,
/// until <see cref="ExpiresAt"/> (24 hours after the link was made).
/// </summary>
/// <remarks>
/// Only the SHA-256 of the token is stored (<see cref="TokenHash"/>), as for API keys, so the link exists in full only
/// in the answer to the member who made it. <see cref="RedeemedAt"/> and <see cref="RedemptionId"/> are set together,
/// once, by a conditional update; the redemption id is what the visitor's browser carries in its grant cookie, so the
/// same browser can open the link again while every other browser is told it was used.
/// </remarks>
public class WallVisitLink
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The one wall the link opens.</summary>
    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    /// <summary>The member (or site admin) who made the link.</summary>
    public Guid CreatedByUserId { get; set; }

    [ForeignKey(nameof(CreatedByUserId))]
    public User CreatedBy { get; set; } = null!;

    /// <summary>Hex SHA-256 of the token. The token itself is never stored.</summary>
    [Required]
    [MaxLength(64)]
    public required string TokenHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When the link and every visit made with it end: <see cref="CreatedAt"/> plus 24 hours.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the link was used, or null while it is unused.</summary>
    public DateTimeOffset? RedeemedAt { get; set; }

    /// <summary>The id of the one redemption, carried in the visitor's grant cookie. Null while unused.</summary>
    public Guid? RedemptionId { get; set; }
}
