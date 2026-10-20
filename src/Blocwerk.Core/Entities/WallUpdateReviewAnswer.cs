// <copyright file="WallUpdateReviewAnswer.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// A person's answer in the one review of a wall update about a hold that is not a card: an old hold not found again,
/// a new detection that goes live, or a detection the triage left out. The answer's effect is written to the session's
/// decision rows in the same transaction; this row says that a person answered (a kept detection has no decision row,
/// and a default discard looks like a confirmed one), who and when, and lets the promote settle the finding the person
/// already answered. Ephemeral working state like the other decision rows: it cascades from the session and the hold,
/// and it is not journalled.
/// </summary>
public class WallUpdateReviewAnswer
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    /// <summary>The hold the answer is about: the old live hold (not found) or the staged detection (new, left out).</summary>
    public Guid HoldId { get; set; }

    public ReviewSubject Subject { get; set; }

    public ReviewAnswer Answer { get; set; }

    public Guid? ByUserId { get; set; }

    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
}
