using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A user's link to their TopLogger account. Stores only encrypted OAuth tokens (never plaintext,
/// never the password) plus the last sync state. One per user. The token protection itself lives in
/// a separate service; the entity only ever holds the resulting ciphertext.
/// </summary>
public class TopLoggerConnection
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    /// <summary>The access token, encrypted at rest (DataProtection ciphertext). Never plaintext.</summary>
    public required string AccessTokenProtected { get; set; }

    /// <summary>The refresh token, encrypted at rest (DataProtection ciphertext). Never plaintext.</summary>
    public required string RefreshTokenProtected { get; set; }

    /// <summary>When the current access token expires, or null when unknown.</summary>
    public DateTimeOffset? AccessExpiresAt { get; set; }

    /// <summary>When the current refresh token expires, or null when unknown.</summary>
    public DateTimeOffset? RefreshExpiresAt { get; set; }

    /// <summary>TopLogger user id, used to filter this user's ascents. Null until first resolved.</summary>
    [MaxLength(64)]
    public string? TopLoggerUserId { get; set; }

    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>Last sync error (e.g. an auth failure), surfaced in the UI. Null when healthy.</summary>
    [MaxLength(1024)]
    public string? LastError { get; set; }

    /// <summary>True once the tokens can no longer be refreshed and the user must reconnect.</summary>
    public bool NeedsReauth { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
