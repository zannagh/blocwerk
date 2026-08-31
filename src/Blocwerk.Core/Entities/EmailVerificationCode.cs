using System.ComponentModel.DataAnnotations;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A one-time, hashed email verification code. Backs the verify-email, password-reset and signup flows
/// (scoped by <see cref="Purpose"/>). The plaintext code is only ever emailed to the address; this row
/// stores only its hash. A row is spent (<see cref="ConsumedUtc"/> set) on a successful verify, when its
/// attempt cap is hit, or when a newer code supersedes it for the same (email, purpose).
/// </summary>
public class EmailVerificationCode
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>Which flow this code belongs to; scoped together with <see cref="Email"/> for lookups.</summary>
    public EmailVerificationPurpose Purpose { get; set; }

    /// <summary>The normalized (trimmed, lower-cased) recipient email. Indexed with <see cref="Purpose"/>.</summary>
    [Required]
    [MaxLength(256)]
    public required string Email { get; set; }

    /// <summary>The KDF hash of the 6-digit code. Never stores the plaintext code.</summary>
    [Required]
    public required string CodeHash { get; set; }

    /// <summary>The account this code belongs to, or null for signup (no account exists yet).</summary>
    public Guid? UserId { get; set; }

    public DateTimeOffset CreatedUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the code stops being accepted (issue time + 10 minutes).</summary>
    public DateTimeOffset ExpiresUtc { get; set; }

    /// <summary>When the code was spent (verified, capped out or superseded), or null while still live.</summary>
    public DateTimeOffset? ConsumedUtc { get; set; }

    /// <summary>How many verify attempts have been made against this code; capped to stop online guessing.</summary>
    public int AttemptCount { get; set; }
}
