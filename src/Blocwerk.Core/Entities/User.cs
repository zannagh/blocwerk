using System.ComponentModel.DataAnnotations;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

public class User : IEquatable<User>
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required]
    [MaxLength(512)]
    public required string Identifier { get; set; }

    [MaxLength(256)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>
    /// A name the user chose for themselves, overriding the OAuth-provided <see cref="DisplayName"/>.
    /// Null (or empty) means "no custom name set" and the app falls back to <see cref="DisplayName"/>.
    /// Read the effective name through <see cref="Name"/>.
    /// </summary>
    [MaxLength(256)]
    public string? CustomDisplayName { get; set; }

    /// <summary>The user's uploaded avatar image bytes, or null when they have none.</summary>
    public byte[]? AvatarImage { get; set; }

    /// <summary>The content type (e.g. image/jpeg) of <see cref="AvatarImage"/>, or null when none.</summary>
    [MaxLength(64)]
    public string? AvatarContentType { get; set; }

    /// <summary>
    /// The user's email address, or null when they have not set one. Stored normalized (trimmed,
    /// lower-cased) and unique (case-insensitively) across users where present. Set only after the
    /// address is confirmed through an email verification code; read the confirmed state via
    /// <see cref="EmailVerified"/>.
    /// </summary>
    [MaxLength(256)]
    public string? Email { get; set; }

    /// <summary>True once the user has confirmed <see cref="Email"/> with a verification code.</summary>
    public bool EmailVerified { get; set; }

    public IdentityRole Role { get; set; } = IdentityRole.User;

    /// <summary>
    /// The username the user chose for password login, or null when they have not set one. Unique
    /// (case-insensitively) across users. Only ever set on an existing OAuth-created user — it is
    /// never a signup path. Read together with <see cref="PasswordHash"/> via <see cref="HasPassword"/>.
    /// </summary>
    [MaxLength(64)]
    public string? LoginUsername { get; set; }

    /// <summary>
    /// The salted KDF hash of the user's login password (produced by IPasswordService), or null when
    /// no password login is configured. Never stores plaintext.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>
    /// The user's TOTP shared secret (Base32), encrypted at rest with DataProtection, or null when no
    /// authenticator has been enrolled. Never stores the raw secret. Read the on/off state through
    /// <see cref="HasTotp"/> — a non-null secret with <see cref="TotpEnabled"/> still false is a pending
    /// enrolment that has not yet been confirmed with a valid code.
    /// </summary>
    public string? TotpSecretProtected { get; set; }

    /// <summary>
    /// True once the user has confirmed their authenticator with a valid code and TOTP is required on
    /// password login. False both when no secret exists and while an enrolment is still pending.
    /// </summary>
    public bool TotpEnabled { get; set; }

    /// <summary>
    /// The last TOTP time-step this user successfully authenticated with, or null if none yet. A verify
    /// is rejected when its matched step is less than or equal to this value, so a still-valid code can
    /// not be replayed inside its ±1 window.
    /// </summary>
    public long? TotpLastUsedStep { get; set; }

    /// <summary>
    /// Consecutive failed password/TOTP attempts in the current lockout window. Reset to 0 on any
    /// successful password or TOTP authentication. Persisted so the cap survives a cookie re-mint.
    /// </summary>
    public int FailedAuthCount { get; set; }

    /// <summary>
    /// When set and in the future, all password and TOTP attempts for this user are rejected until it
    /// passes (a server-side brute-force lockout). Null when the account is not locked.
    /// </summary>
    public DateTimeOffset? LockoutUntil { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public int ProgressionWindowDays { get; set; } = 60;

    public ProgressionGroupBy ProgressionGroupBy { get; set; } = ProgressionGroupBy.Week;

    /// <summary>
    /// The wall this user considers their home wall, or null if none is set. Points at a wall the
    /// user is a member of; cleared (set to null) if that wall is deleted.
    /// </summary>
    public Guid? HomeWallId { get; set; }

    /// <summary>
    /// The user's preferred bouldering grade scale. True (the default) seeds grade inputs with the
    /// Fontainebleau scale; false seeds them with the V-Scale. Only a UI default — a boulder's grade
    /// is still stored as a free string and can be entered on either scale.
    /// </summary>
    public bool PreferFontGrades { get; set; } = true;

    /// <summary>
    /// The OAuth provider identities linked to this user. A user is born from one OAuth login and may
    /// later own several (account linking). Legacy users and dev logins may have none until their next
    /// login attaches one lazily.
    /// </summary>
    public ICollection<UserIdentity> Identities { get; set; } = [];

    public ICollection<WallMember> WallMemberships { get; set; } = [];

    public ICollection<Attempt> Attempts { get; set; } = [];

    public ICollection<HangboardSession> HangboardSessions { get; set; } = [];

    public ICollection<PullupSession> PullupSessions { get; set; } = [];

    /// <summary>
    /// The effective display name: the user-chosen <see cref="CustomDisplayName"/> when set,
    /// otherwise the OAuth-provided <see cref="DisplayName"/> fallback.
    /// </summary>
    public string Name => string.IsNullOrWhiteSpace(CustomDisplayName) ? DisplayName : CustomDisplayName;

    /// <summary>True when the user has an avatar image stored (drives the avatar rendering path).</summary>
    public bool HasAvatar => AvatarImage is { Length: > 0 };

    /// <summary>True when the user has configured a login username + password (drives the profile UI state).</summary>
    public bool HasPassword => !string.IsNullOrEmpty(PasswordHash);

    /// <summary>True when TOTP two-factor is enabled on this account (drives the profile UI + login challenge).</summary>
    public bool HasTotp => TotpEnabled;

    public string UserName => Identifier.Split("__").FirstOrDefault() ?? Identifier;

    public string UserAuthId => Identifier.Split("__").LastOrDefault() ?? Identifier;

    public bool Equals(User? other) => other is not null && GetType() == other.GetType() && Id == other.Id;

    public override bool Equals(object? obj) => obj is User other && Equals(other);

    public override int GetHashCode() => Id.GetHashCode();
}
