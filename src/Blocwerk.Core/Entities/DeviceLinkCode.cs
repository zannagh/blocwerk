// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A one-time code that links a device (the companion iPhone app) to its owner's account: made when the signed-in
/// user confirms the link in the browser, exchanged by the app for a personal API key.
/// </summary>
/// <remarks>
/// Only the SHA-256 of the code is stored (<see cref="CodeHash"/>), as for API keys and visitor links. The code is
/// bound to the user who confirmed it and to the PKCE challenge the app sent, lives 60 seconds and is used at most
/// once: <see cref="UsedAt"/> is set by one conditional update, so of two concurrent exchanges exactly one wins.
/// </remarks>
public class DeviceLinkCode
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The user who confirmed the link; the key is minted for them.</summary>
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    /// <summary>Hex SHA-256 of the code. The code itself is never stored.</summary>
    [Required]
    [MaxLength(64)]
    public required string CodeHash { get; set; }

    /// <summary>The app's S256 PKCE challenge (URL-safe base64 of the verifier's SHA-256).</summary>
    [Required]
    [MaxLength(64)]
    public required string CodeChallenge { get; set; }

    /// <summary>The device name the user saw on the consent page; the key is named after it.</summary>
    [Required]
    [MaxLength(64)]
    public required string DeviceName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>When the code was exchanged (successfully or not), or null while unused.</summary>
    public DateTimeOffset? UsedAt { get; set; }
}
