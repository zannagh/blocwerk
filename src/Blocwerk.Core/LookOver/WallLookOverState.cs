// <copyright file="WallLookOverState.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

namespace Blocwerk.Core.LookOver;

/// <summary>The wall's look-over flag, as the wall page and scripts read it.</summary>
/// <param name="Pending">The wall waits for a look-over.</param>
/// <param name="PendingSince">Since when (the first update that left a finding after the last finished look-over).</param>
/// <param name="Generation">The wall generation of the latest update that left a finding.</param>
/// <param name="OpenFindings">Findings nobody answered yet.</param>
/// <param name="TotalFindings">The open findings plus those answered since the wall was flagged.</param>
/// <param name="CompletedAt">When the last look-over was finished.</param>
/// <param name="CompletedBy">Who finished it.</param>
/// <param name="CanLookOver">The user may open the look-over (owner, wall admin, moderator, installation admin).</param>
/// <param name="BouldersMarked">Current boulders of the wall that are marked for review (0 while the wall is not flagged).</param>
/// <param name="MoreBoulders">Current boulders that are NOT marked but use a hold an open finding is about.</param>
public sealed record WallLookOverState(
    bool Pending,
    DateTimeOffset? PendingSince,
    int? Generation,
    int OpenFindings,
    int TotalFindings,
    DateTimeOffset? CompletedAt,
    string? CompletedBy,
    bool CanLookOver,
    int BouldersMarked = 0,
    int MoreBoulders = 0);
