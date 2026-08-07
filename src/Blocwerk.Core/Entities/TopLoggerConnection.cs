using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A user's link to their TopLogger account. Stores only the API token (encrypted at rest via
/// <see cref="Services.ITokenProtector"/>) — never the password — plus which backend authenticated
/// and the last sync state. One per user.
/// </summary>
public class TopLoggerConnection
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    [MaxLength(512)]
    public required string Email { get; set; }

    /// <summary>TopLogger numeric user id, used to filter ascents.</summary>
    [MaxLength(64)]
    public string? UserUid { get; set; }

    /// <summary>The TopLogger API token, encrypted. Never logged, never stored in plaintext.</summary>
    public required string TokenEncrypted { get; set; }

    public TopLoggerBackend Backend { get; set; } = TopLoggerBackend.Unknown;

    public DateTimeOffset? LastSyncAt { get; set; }

    /// <summary>Last sync error (e.g. "reconnect needed" on a 401), surfaced in the UI. Null when healthy.</summary>
    [MaxLength(1024)]
    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
