// Copyright (c) 2026, zannagh. All rights reserved.
// See License in the project root for license information.

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace Blocwerk.Core.Entities;

/// <summary>
/// An Apple ID linked to a Blocwerk account, so the companion app can sign in with Apple on this instance.
/// </summary>
/// <remarks>
/// <see cref="Subject"/> is Apple's stable user id (the identity token's <c>sub</c>) for this app's team. It is unique,
/// so one Apple ID opens at most one account, and <see cref="UserId"/> is unique, so an account has at most one Apple
/// ID. A device links one only while the account has none (another is unlinked on the web first). The e-mail Apple
/// sends is never stored or matched.
/// </remarks>
public class AppleLink
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>The account the Apple ID signs in to.</summary>
    public Guid UserId { get; set; }

    [ForeignKey(nameof(UserId))]
    public User User { get; set; } = null!;

    /// <summary>Apple's user id, the verified identity token's <c>sub</c>.</summary>
    [Required]
    [MaxLength(255)]
    public required string Subject { get; set; }

    public DateTimeOffset LinkedAt { get; set; }

    /// <summary>The name of the device key that linked it ("iPhone: Anna's iPhone"), shown on the web profile.</summary>
    [MaxLength(100)]
    public string? LinkedFrom { get; set; }
}
