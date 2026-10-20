// <copyright file="LookOverAnswerApiContracts.cs" company="Blocwerk">
// Copyright (c) Blocwerk. All rights reserved.
// </copyright>

using System.Text.Json.Serialization;
using Blocwerk.Core.Enums;

namespace Blocwerk.Web.Controllers;

/// <summary>Body of <c>POST …/look-over/findings/{findingId}/answer</c>.</summary>
/// <param name="Answer">
/// One of the finding's <c>answers</c>: <c>Settle</c>, <c>DifferentHold</c>, <c>RemoveHold</c>, <c>UseNewHold</c>,
/// <c>AddHold</c>, <c>Unlink</c> (names or numbers).
/// </param>
/// <param name="ExpectedBoulderIds">
/// The ids of the finding's <c>boulders</c> as read. Required for every answer but <c>Settle</c>: when other boulders
/// use the hold by now, the answer is refused (409) and the caller reads the findings again.
/// </param>
/// <param name="NewHoldBoulderIds">
/// With <c>UseNewHold</c> only, asked per boulder ("Should this boulder use the new hold in that place?"): the boulders
/// that should use the new hold, a subset of <c>expectedBoulderIds</c> (400 otherwise). The others become historic with
/// the old hold, which is removed; an empty list is <c>RemoveHold</c>. Omitted or null moves every boulder (the merge).
/// </param>
/// <param name="TargetHoldId">
/// A hold of the finding's photo (from <c>GET …/look-over/holds?panelId=</c> or a finding's <c>otherHoldId</c>), not a kept
/// copy. With <c>Reassign</c> ("It is this hold", required): the hold the update could not find IS this hold, and is merged
/// into it (wall admins). With <c>DifferentHold</c> (optional): the unsure match is another hold, and THIS hold replaced
/// it, so a following <c>UseNewHold</c> (per boulder too) moves boulders onto it rather than onto the detection the update
/// paired it with.
/// </param>
/// <param name="Spot">
/// With <c>Moved</c> ("It is here", required; <c>AddHold</c> with a spot means the same): where the hold the update could
/// not find really is, on its own photo and on bare wall (a spot on a hold is <c>Reassign</c>).
/// </param>
public sealed record LookOverAnswerRequest(
    [property: JsonConverter(typeof(JsonStringEnumConverter<LookOverAnswer>))] LookOverAnswer? Answer,
    IReadOnlyCollection<Guid>? ExpectedBoulderIds = null,
    IReadOnlyCollection<Guid>? NewHoldBoulderIds = null,
    Guid? TargetHoldId = null,
    LookOverSpotRequest? Spot = null);

/// <summary>A spot on a panel photo, for "It is here".</summary>
/// <param name="PanelId">The photo (the finding's <c>panelId</c>); optional, refused when it is another photo.</param>
/// <param name="X">Across the photo, 0..1.</param>
/// <param name="Y">Down the photo, 0..1.</param>
/// <param name="R">The hold's radius there in photo widths (0..0.5]; optional, the hold keeps its size without it.</param>
public sealed record LookOverSpotRequest(Guid? PanelId, double? X, double? Y, double? R = null);

/// <summary>Body of <c>POST …/look-over/finish</c>.</summary>
/// <param name="LeaveOpen">Finish although findings are open (they stay open and listed). Defaults to false.</param>
public sealed record LookOverFinishRequest(bool LeaveOpen = false);
