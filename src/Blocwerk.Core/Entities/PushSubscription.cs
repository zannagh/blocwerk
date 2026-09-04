using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A single browser Push API subscription belonging to a user. One row per device/browser (a user
/// may have several), keyed for delivery by its <see cref="Endpoint"/>. Cascade-deleted with the
/// user. The <see cref="P256dh"/>/<see cref="Auth"/> pair are the RFC 8291 encryption keys the
/// server needs to encrypt a payload for this endpoint.
/// </summary>
public class PushSubscription
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    /// <summary>The push service URL to POST the encrypted message to. Unique across all rows.</summary>
    [Required]
    [MaxLength(1024)]
    public required string Endpoint { get; set; }

    /// <summary>The client's public P-256 ECDH key (Base64url), part of the RFC 8291 payload encryption.</summary>
    [Required]
    [MaxLength(256)]
    public required string P256dh { get; set; }

    /// <summary>The client's auth secret (Base64url), part of the RFC 8291 payload encryption.</summary>
    [Required]
    [MaxLength(256)]
    public required string Auth { get; set; }

    /// <summary>The user-agent that created the subscription, or null when unknown. Informational only.</summary>
    [MaxLength(512)]
    public string? UserAgent { get; set; }

    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>When this subscription last re-announced itself, or null if never since creation.</summary>
    public DateTime? LastSeenUtc { get; set; }
}
