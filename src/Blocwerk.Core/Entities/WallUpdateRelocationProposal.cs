// <copyright file="WallUpdateRelocationProposal.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// One "this hold moved" suggestion of a <see cref="WallUpdateSession"/>: an old live hold the matcher
/// found no positional counterpart for ("disappeared") paired, by appearance fingerprint alone, with a
/// staged hold that matched no old hold ("appeared"). Computed ONCE per session (see
/// <see cref="WallUpdateSession.RelocationsProposedAt"/>) so a resumed review shows the same list.
/// <para>
/// A suggestion only: nothing happens until a person accepts it, which records a
/// <see cref="CarryKind.Changed"/> carry verdict old → new (decision D-A: moved == changed). Like the
/// other decision rows this is ephemeral working state — real FKs that CASCADE from both hold ends, so a
/// staged hold deleted mid-session takes its suggestion with it. Not change-journalled.
/// </para>
/// </summary>
public class WallUpdateRelocationProposal
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    /// <summary>The old live (gen-N) hold that disappeared.</summary>
    public Guid OldHoldId { get; set; }

    [ForeignKey(nameof(OldHoldId))]
    public Hold OldHold { get; set; } = null!;

    /// <summary>The staged (gen-N+1) hold proposed to be the same physical hold, moved.</summary>
    public Guid NewHoldId { get; set; }

    [ForeignKey(nameof(NewHoldId))]
    public Hold NewHold { get; set; } = null!;

    /// <summary>The fingerprint similarity of the pair (0..1).</summary>
    public double Score { get; set; }

    /// <summary>How far the pair beats its best competitor sharing either hold (1 when uncontested).</summary>
    public double Margin { get; set; }

    /// <summary>
    /// True when both fingerprints carried millimetre sizes on a marker wall, so size took part in the
    /// score. False = colour + shape only: look-alike holds can fool it, so the UI labels it lower-confidence.
    /// </summary>
    public bool Metric { get; set; }

    public RelocationProposalStatus Status { get; set; } = RelocationProposalStatus.Pending;

    public Guid? DecidedByUserId { get; set; }

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
