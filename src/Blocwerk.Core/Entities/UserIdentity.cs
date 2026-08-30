using System.ComponentModel.DataAnnotations;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A single OAuth provider identity owned by a <see cref="User"/>. One user may own several of
/// these (e.g. a GitHub and a Google login), which is the foundation for account linking/merge.
/// A row ties the stable provider-issued subject (<see cref="Provider"/> + <see cref="ProviderUserId"/>)
/// to the local user, so a returning login resolves to the same account regardless of which of the
/// user's linked providers was used.
/// </summary>
public class UserIdentity
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The local user that owns this provider identity.</summary>
    public Guid UserId { get; set; }

    /// <summary>Navigation to the owning <see cref="User"/>.</summary>
    public User User { get; set; } = null!;

    /// <summary>The OAuth provider key, e.g. "github", "google" or "microsoft".</summary>
    [Required]
    [MaxLength(32)]
    public required string Provider { get; set; }

    /// <summary>The provider-issued stable user id (the OAuth subject / nameid).</summary>
    [Required]
    [MaxLength(256)]
    public required string ProviderUserId { get; set; }

    /// <summary>When this provider identity was first attached to the user.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
