// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A nonce the server issued for one Sign in with Apple: the app hashes it into its Apple request, so the identity
/// token Apple returns carries the hash and cannot be replayed with another nonce.
/// </summary>
/// <remarks>
/// Only the hex SHA-256 is stored (<see cref="NonceHash"/>), which is also exactly the value the token's <c>nonce</c>
/// claim carries. A nonce lives five minutes and is used at most once: <see cref="UsedAt"/> is set by one conditional
/// update, so of two concurrent uses exactly one wins.
/// </remarks>
public class AppleSignInNonce
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Hex SHA-256 of the nonce. The nonce itself is never stored.</summary>
    [Required]
    [MaxLength(64)]
    public required string NonceHash { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the nonce was used, or null while unused.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}
