// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A spent Sign in with Apple nonce, kept until it would have expired so it cannot be used again. Unspent nonces are
/// never stored: they are signed by the server and carry their own expiry.
/// </summary>
public class AppleSignInNonce
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Hex SHA-256 of the nonce, unique, so a second use fails to insert.</summary>
    [Required]
    [MaxLength(64)]
    public required string NonceHash { get; set; }

    /// <summary>When the nonce expires; the row can go then.</summary>
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the nonce was spent.</summary>
    public DateTimeOffset UsedAt { get; set; }
}
