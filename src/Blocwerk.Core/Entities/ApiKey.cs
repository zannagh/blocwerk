using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A long-lived bearer token for machine callers (sensors posting temperatures, cameras posting
/// wall images, scripts driving the API).
/// </summary>
/// <remarks>
/// Only the SHA-256 of the token is stored, so the full value exists exactly once — in the
/// response of the call that created it. Tokens are formatted <c>bwk_&lt;64 hex chars&gt;</c>;
/// the <see cref="TokenPrefix"/> is load-bearing because the auth layer uses it to tell an API
/// key apart from a JWT inside an <c>Authorization: Bearer</c> header.
/// </remarks>
public class ApiKey
{
    /// <summary>Marks a bearer value as a Blocwerk API key rather than a JWT.</summary>
    public const string TokenPrefix = "bwk_";

    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(100)]
    public required string Name { get; set; }

    public ApiKeyScope Scope { get; set; } = ApiKeyScope.User;

    /// <summary>The user who created and owns the key, whatever its scope.</summary>
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    /// <summary>
    /// Set for <see cref="ApiKeyScope.Wall"/> and <see cref="ApiKeyScope.Kiosk"/> keys; null otherwise.
    /// </summary>
    public Guid? WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall? Wall { get; set; }

    /// <summary>Hex SHA-256 of the full token. The token itself is never stored.</summary>
    [Required]
    [MaxLength(128)]
    public required string KeyHash { get; set; }

    /// <summary>The displayable leading characters, so a listing can hint at which key is which.</summary>
    [Required]
    [MaxLength(16)]
    public required string Prefix { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Stamped on use, but throttled — a busy sensor must not cause a write per request.</summary>
    public DateTimeOffset? LastUsedAt { get; set; }

    public DateTimeOffset? ExpiresAt { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
}
