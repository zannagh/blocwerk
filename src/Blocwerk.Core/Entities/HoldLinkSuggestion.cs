// <copyright file="HoldLinkSuggestion.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Blocwerk.Core.Enums;

namespace Blocwerk.Core.Entities;

/// <summary>
/// Two holds on different panel photos whose 3D positions coincide, so they are probably one physical hold that
/// is not linked yet (<see cref="HoldLinks.HoldLinkSuggestionFinder"/>). Never a link by itself: a wall admin links
/// the pair through the normal linking path or answers "not the same", which is remembered. The pair is unordered
/// and stored with <see cref="HoldAId"/> &lt; <see cref="HoldBId"/>; the hold ids are plain ids (no foreign key), so
/// deleting a hold never has to know about suggestions — a refresh drops rows of holds that are gone.
/// </summary>
public class HoldLinkSuggestion
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid WallId { get; set; }

    [ForeignKey(nameof(WallId))]
    public Wall Wall { get; set; } = null!;

    public Guid HoldAId { get; set; }

    public Guid HoldBId { get; set; }

    /// <summary>How far apart the two holds sit in 3D when last checked, mm.</summary>
    public double DistanceMm { get; set; }

    public HoldLinkSuggestionStatus Status { get; set; }

    public Guid? ReviewedByUserId { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
