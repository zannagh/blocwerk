using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

public class WallMember
{
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public WallRole Role { get; set; } = WallRole.Member;

    public DateTimeOffset JoinedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// When this member agreed to be offered as a pickable user on the wall's kiosk tablets. Null means
    /// no consent, which is the default and what a revocation returns to. Consent is per member row, so
    /// it never leaks to the user's other walls.
    /// </summary>
    public DateTimeOffset? KioskConsentedAt { get; set; }

    /// <summary>
    /// Optional salted hash of the short PIN the member must type to be picked at the kiosk. Null means
    /// no PIN, i.e. anyone at the tablet may pick them. The PIN itself is never stored.
    /// </summary>
    [MaxLength(256)]
    public string? KioskPinHash { get; set; }

    /// <summary>
    /// How many digits the kiosk PIN has, or 0 when there is no PIN. The tablet needs this — and only
    /// this — to know when an entry is complete, so it can submit once instead of guessing at every
    /// keystroke and burning throttle attempts. The PIN itself stays in the hash and never leaves the
    /// server.
    /// </summary>
    public int KioskPinLength { get; set; }
}
